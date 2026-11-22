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

    // BlazePose still reports elbows and wrists that are below the frame, often with high
    // visibility, as guesses pinned near the bottom edge. Driving the arms from those guesses
    // threw a seated performer's arms sideways. A pose landmark must first appear this far
    // inside the frame (fractions of the image height) and is dropped once it leaves the frame.
    // A detected hand still drives its arm from the hand landmarks.
    const float EnterMarginBottom = .12f;
    const float EnterMarginSide = .05f;
    const float StayMargin = .015f;

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
    // The hand detector drops a blurred hand or a fist for a few frames and finds it again.
    // Holding the last hand that long keeps the arm and fingers from flicking to rest and back.
    const int HandHoldSamples = 6;

    // Pose arm landmarks for one side, as offsets from the performer's left-side index.
    static readonly int[] ArmLandmarks =
    [
        PoseLandmark.LeftElbow,
        PoseLandmark.LeftWrist,
        PoseLandmark.LeftPinky,
        PoseLandmark.LeftIndex,
        PoseLandmark.LeftThumb,
    ];
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

    // `armsNeedHands`: in half-body framing BlazePose confidently reports a resting arm's wrist
    // on the chest (visibility above 0.9), which raised a phantom arm. The hand detector produced
    // no false hands in the same recordings, so an arm only follows the pose while its hand is
    // detected or briefly held. Full-body framing keeps pose-only arms: distant hands are small.
    public Motion Step(Motion raw, float dt, bool armsNeedHands = false)
    {
        dt = Math.Clamp(dt, 1f / 240, .25f);
        float unit = Math.Max(1, raw.Height);
        var visibility = Gate(raw);
        var filteredHands = new List<Hand>(2);
        bool[] present = new bool[2];
        foreach (var h in ConsistentSides(raw.Hands))
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
        // Gate returns its own array (and sizes `held`) whenever there is a full pose.
        if (
            armsNeedHands
            && visibility.Length == PoseLandmark.Count
            && held.Length == PoseLandmark.Count
        )
            for (int side = 0; side < 2; side++)
                if (!filteredHands.Any(h => h.Left == (side == 0)))
                    foreach (int landmark in ArmLandmarks)
                    {
                        visibility[landmark + side] = 0;
                        // Re-entering must pass the full threshold again.
                        held[landmark + side] = 0;
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

    // The handedness model now and then labels the only visible hand as the other side for a
    // frame. The arm that owned it then dropped while the opposite arm leapt across to it. A hand
    // that appears where the other side's hand just was, while that hand is gone, keeps the
    // other side's label; two labels on one spot are the same hand.
    IEnumerable<Hand> ConsistentSides(Hand[] detected)
    {
        static Vector2 Wrist(Hand h) => new(h.Points[HandLandmark.Wrist].X, h.Points[HandLandmark.Wrist].Y);
        static float Palm(Hand h) =>
            Math.Max(
                8,
                Vector2.Distance(
                    Wrist(h),
                    new(h.Points[HandLandmark.MiddleBase].X, h.Points[HandLandmark.MiddleBase].Y)
                )
            );
        var hands = detected.Where(h => h.Points.Length == HandLandmark.Count).ToList();
        if (hands.Count == 2 && Vector2.Distance(Wrist(hands[0]), Wrist(hands[1])) < Palm(hands[0]))
        {
            // Keep the label that continues a hand from the previous sample, else the stronger.
            float Continuity(Hand h) =>
                lastHand[h.Left ? 0 : 1] is { } last
                    ? Vector2.Distance(Wrist(last), Wrist(h)) / Palm(h)
                    : float.PositiveInfinity;
            hands = [hands.OrderBy(Continuity).ThenByDescending(h => h.Confidence).First()];
        }
        if (hands.Count == 1)
        {
            var h = hands[0];
            int side = h.Left ? 0 : 1,
                other = 1 - side;
            float Gap(Hand? last) =>
                last is null ? float.PositiveInfinity : Vector2.Distance(Wrist(last), Wrist(h));
            if (Gap(lastHand[other]) < Palm(h) * 1.5f && Gap(lastHand[other]) < Gap(lastHand[side]))
                hands[0] = h with { Left = other == 0 };
        }
        return hands;
    }

    // Decides per pose landmark whether the solver may treat it as seen, with hysteresis on both
    // the visibility score and the distance from the frame edge. Unseen landmarks report 0.
    float[] Gate(Motion raw)
    {
        var score = raw.Visibility;
        if (score.Length == 0 || raw.Body.Length != score.Length)
        {
            held = [];
            return score;
        }
        if (held.Length != score.Length)
            held = new float[score.Length];
        float h = raw.Height;
        float Inset(Vector3 p) => Math.Min(Math.Min(p.X, raw.Width - p.X), Math.Min(p.Y, h - p.Y));
        var result = new float[score.Length];
        for (int i = 0; i < score.Length; i++)
        {
            var p = raw.Body[i];
            bool wasSeen = held[i] >= Motion.VisibleScore;
            bool enters =
                score[i] >= Motion.VisibleScore
                && Math.Min(p.X, raw.Width - p.X) >= EnterMarginSide * h
                && p.Y >= EnterMarginSide * h
                && h - p.Y >= EnterMarginBottom * h;
            bool stays = wasSeen && score[i] >= HoldScore && Inset(p) >= StayMargin * h;
            bool seen = float.IsFinite(p.X) && float.IsFinite(p.Y) && (enters || stays);
            result[i] = seen ? Math.Max(score[i], Motion.VisibleScore) : 0;
            held[i] = result[i];
        }
        return result;
    }
}
