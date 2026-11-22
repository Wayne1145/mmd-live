using System.Numerics;
using MmdLive.Tracking;

namespace MmdLive.Rig;

// One Euro filter (Casiez et al. 2012): a low-pass filter whose cutoff rises with speed. A
// still joint is smoothed hard, a fast gesture passes with little lag.
internal struct OneEuro
{
    float value,
        speed;
    bool primed;

    public void Reset() => primed = false;

    static float Alpha(float cutoff, float dt) => 1 / (1 + 1 / (2 * MathF.PI * cutoff * dt));

    // Filters one channel. `rate` is the channel's speed in its own units per second, measured
    // over the whole landmark so x and y share one cutoff.
    public float Step(float raw, float dt, float minCutoff, float beta, float rate)
    {
        if (!primed || !float.IsFinite(value))
        {
            (value, speed, primed) = (raw, 0, true);
            return raw;
        }
        speed += Alpha(1, dt) * (rate - speed);
        value += Alpha(minCutoff + beta * speed, dt) * (raw - value);
        return value;
    }
}

internal readonly record struct FilterTuning(float MinCutoff, float Beta, float DepthCutoff);

// Smooths landmarks between the tracker and the solver. Raw MediaPipe output is noisy frame to
// frame, and its depth flips sign: a wrist reads 30 cm in front of the shoulder, then 30 cm
// behind. The solver follows every target at its turn limit, so unfiltered noise shows as the
// avatar snapping around. Filtering here keeps the tracker's own output raw for the preview.
internal sealed class MotionFilter
{
    // Image points are measured in image heights so the tuning does not depend on resolution.
    // Tuned with `jitter` on a waving clip. Body and world use low beta: MediaPipe depth flips
    // look like very fast motion, and a high beta would let exactly those flips through.
    internal static readonly FilterTuning Body = new(.5f, .5f, .2f);
    internal static readonly FilterTuning Face = new(2f, 6f, .8f);
    internal static readonly FilterTuning Hands = new(2.2f, 8f, .8f);
    // World landmarks are meters around the hips. Their depth is the least stable channel.
    internal static readonly FilterTuning World = new(.3f, .3f, .15f);

    // A landmark becomes seen at Motion.VisibleScore and stays seen until it falls below this.
    // Visibility on a blurred arm hovers around the threshold and would toggle the limb every frame.
    const float HoldScore = .3f;

    sealed class PointSet
    {
        OneEuro[] x = [],
            y = [],
            z = [];
        Vector3[] last = [];

        public void Reset()
        {
            for (int i = 0; i < x.Length; i++)
            {
                x[i].Reset();
                y[i].Reset();
                z[i].Reset();
            }
            last = [];
        }

        public Vector3[] Step(Vector3[] raw, float dt, float unit, FilterTuning t)
        {
            if (raw.Length == 0)
            {
                Reset();
                return raw;
            }
            if (x.Length != raw.Length)
            {
                (x, y, z) = (new OneEuro[raw.Length], new OneEuro[raw.Length], new OneEuro[raw.Length]);
                last = [];
            }
            var result = new Vector3[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                var p = raw[i] / unit;
                float rate =
                    last.Length == raw.Length
                        ? Vector2.Distance(new(p.X, p.Y), new(last[i].X, last[i].Y)) / dt
                        : 0;
                float depthRate = last.Length == raw.Length ? Math.Abs(p.Z - last[i].Z) / dt : 0;
                result[i] =
                    new Vector3(
                        x[i].Step(p.X, dt, t.MinCutoff, t.Beta, rate),
                        y[i].Step(p.Y, dt, t.MinCutoff, t.Beta, rate),
                        z[i].Step(p.Z, dt, t.DepthCutoff, t.Beta, depthRate)
                    ) * unit;
            }
            last = raw.Select(p => p / unit).ToArray();
            return result;
        }
    }

    readonly PointSet body = new(),
        world = new(),
        face = new();
    readonly PointSet[] hands = [new(), new()];
    // The hand detector drops a blurred hand for a frame or two and finds it again. Holding the
    // last hand that long keeps the wrist and fingers from flicking to rest and back.
    const int HandHoldSamples = 3;
    readonly Hand?[] lastHand = new Hand?[2];
    readonly int[] missing = new int[2];
    float[] held = [];

    public void Reset()
    {
        body.Reset();
        world.Reset();
        face.Reset();
        foreach (var h in hands)
            h.Reset();
        Array.Clear(lastHand);
        Array.Clear(missing);
        held = [];
    }

    public Motion Step(Motion raw, float dt)
    {
        dt = Math.Clamp(dt, 1f / 240, .25f);
        float unit = Math.Max(1, raw.Height);
        var visibility = Hold(raw.Visibility);
        var filteredHands = new List<Hand>(2);
        bool[] present = new bool[2];
        foreach (var h in raw.Hands)
        {
            int side = h.Left ? 0 : 1;
            present[side] = true;
            missing[side] = 0;
            var smooth = h with { Points = hands[side].Step(h.Points, dt, unit, Hands) };
            lastHand[side] = smooth;
            filteredHands.Add(smooth);
        }
        for (int side = 0; side < 2; side++)
        {
            if (present[side] || lastHand[side] is not { } last)
                continue;
            if (++missing[side] <= HandHoldSamples)
                filteredHands.Add(last with { Held = true });
            else
            {
                // A hand that stayed away starts fresh when it returns, possibly somewhere else.
                hands[side].Reset();
                lastHand[side] = null;
            }
        }
        return raw with
        {
            Body = body.Step(raw.Body, dt, unit, Body),
            Face = face.Step(raw.Face, dt, unit, Face),
            Visibility = visibility,
            Hands = filteredHands.ToArray(),
            WorldBody = world.Step(raw.WorldBody, dt, 1, World),
        };
    }

    float[] Hold(float[] raw)
    {
        if (raw.Length == 0)
        {
            held = [];
            return raw;
        }
        if (held.Length != raw.Length)
            held = new float[raw.Length];
        var result = (float[])raw.Clone();
        for (int i = 0; i < raw.Length; i++)
        {
            bool seen =
                raw[i] >= Motion.VisibleScore
                || (held[i] >= Motion.VisibleScore && raw[i] >= HoldScore);
            if (seen && raw[i] < Motion.VisibleScore)
                result[i] = Motion.VisibleScore;
            held[i] = seen ? Math.Max(raw[i], Motion.VisibleScore) : raw[i];
        }
        return result;
    }
}
