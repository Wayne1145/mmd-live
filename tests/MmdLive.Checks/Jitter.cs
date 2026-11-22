using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using MmdLive.Rig;
using MmdLive.Tracking;

namespace MmdLive.Checks;

// Measures how jumpy the live avatar is on recorded observations: a 30 fps source drawn by a
// 60 Hz render loop, exactly as in the studio. Reports, per joint, angular travel, single-frame
// jumps and back-and-forth reversals, plus how far the rig wrist trails the observed wrist.
internal static class Jitter
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static readonly string[] Joints =
    [
        "上半身",
        "頭",
        "左腕",
        "左ひじ",
        "左手首",
        "右腕",
        "右ひじ",
        "右手首",
    ];

    internal static void Run(string model, string observations, bool full, string? frames = null)
    {
        var rig = new AvatarRig(model);
        // Optional: one PNG per 60 Hz frame, to watch the motion instead of reading numbers.
        using var renderer = frames == null
            ? null
            : new MmdLive.Rendering.Renderer(0, MmdLive.Rendering.GraphicsAdapter.List().First().Index, 640, 360)
            {
                FullBody = full,
            };
        if (frames != null)
        {
            Directory.CreateDirectory(frames);
            renderer!.Load(rig);
        }
        int[] joints = Joints.Select(n => rig.Model.Bones.FindIndex(b => b.Name == n)).ToArray();
        if (joints.Contains(-1))
            throw new InvalidDataException("Jitter check requires a standard MMD upper body");
        Quaternion Rotation(int i) =>
            Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rig.Skin[i]));
        static float Angle(Quaternion a, Quaternion b) =>
            360 / MathF.PI * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a, b)), 0, 1));

        long tick = Stopwatch.Frequency / 30,
            render = Stopwatch.Frequency / 60;
        var travel = new double[joints.Length];
        var jumps = new int[joints.Length];
        var reversals = new int[joints.Length];
        Quaternion[]? previous = null;
        Vector3[]? lastAxis = null;
        var wristError = new List<float>();
        int rendered = 0,
            sample = 0,
            handToggles = 0;
        bool[] hadHand = new bool[2];
        foreach (var line in File.ReadLines(observations))
        {
            var motion = JsonSerializer.Deserialize<Motion>(line, Json)!;
            for (int s = 0; s < 2; s++)
            {
                bool has = motion.Hands.Any(h => h.Left == (s == 0));
                if (sample > 0 && has != hadHand[s])
                    handToggles++;
                hadHand[s] = has;
            }
            long stamp = (sample + 1) * tick;
            var input = new InputSample(motion, stamp);
            foreach (long now in new[] { stamp, stamp + render })
            {
                var trace = rig.ApplySample(input, full, now);
                var current = joints.Select(Rotation).ToArray();
                if (previous != null)
                {
                    lastAxis ??= new Vector3[joints.Length];
                    for (int j = 0; j < joints.Length; j++)
                    {
                        float step = Angle(current[j], previous[j]);
                        travel[j] += step;
                        // 8° in one 60 Hz frame is 480°/s: a snap, or a very fast gesture.
                        if (step > 8)
                        {
                            jumps[j]++;
                            if (Environment.GetEnvironmentVariable("JITTER_LIST") == "1")
                                Console.WriteLine(
                                    $"sample {sample,4} {Joints[j]} {step,5:0.0} deg  hands L={motion.Hands.Any(h => h.Left)} R={motion.Hands.Any(h => !h.Left)}  vis el {motion.Visibility.ElementAtOrDefault(13):0.00}/{motion.Visibility.ElementAtOrDefault(14):0.00} wr {motion.Visibility.ElementAtOrDefault(15):0.00}/{motion.Visibility.ElementAtOrDefault(16):0.00}"
                                );
                        }
                        var delta = current[j] * Quaternion.Inverse(previous[j]);
                        if (delta.W < 0)
                            delta = -delta;
                        var axis = new Vector3(delta.X, delta.Y, delta.Z);
                        if (step > 1.5f)
                        {
                            axis = Vector3.Normalize(axis);
                            if (Vector3.Dot(axis, lastAxis[j]) < -.5f)
                                reversals[j]++;
                            lastAxis[j] = axis;
                        }
                    }
                }
                previous = current;
                if (renderer != null)
                {
                    renderer.Render();
                    renderer.SavePng(Path.Combine(frames!, $"{rendered:D5}.png"));
                }
                rendered++;
                if (now == stamp)
                {
                    var snapshot = rig.Snapshot(trace, motion);
                    foreach (var hand in motion.Hands)
                    {
                        int wrist = joints[hand.Left ? 4 : 7];
                        if (snapshot.Alignment is { } a)
                        {
                            var p = Vector3.Transform(rig.Model.Bones[wrist].Position, rig.Skin[wrist]);
                            wristError.Add(
                                100
                                    * Vector2.Distance(a.Pixel(p), new(hand.Points[0].X, hand.Points[0].Y))
                                    / motion.Width
                            );
                        }
                    }
                }
            }
            sample++;
        }
        double seconds = rendered / 60.0;
        Console.WriteLine(
            $"{sample} samples, {seconds:0.0} s, hand appear/disappear events {handToggles}"
        );
        Console.WriteLine($"{"joint", -8} {"deg/s", 8} {"jumps", 6} {"reversals", 10}");
        for (int j = 0; j < joints.Length; j++)
            Console.WriteLine(
                $"{Joints[j], -8} {travel[j] / seconds, 8:0.0} {jumps[j], 6} {reversals[j], 10}"
            );
        Console.WriteLine(
            $"{"total", -8} {travel.Sum() / seconds, 8:0.0} {jumps.Sum(), 6} {reversals.Sum(), 10}"
        );
        if (wristError.Count > 0)
        {
            wristError.Sort();
            Console.WriteLine(
                $"wrist error vs observed hand: median {wristError[wristError.Count / 2]:0.00}% p90 {wristError[(int)(wristError.Count * .9)]:0.00}% of frame width"
            );
        }
    }
}
