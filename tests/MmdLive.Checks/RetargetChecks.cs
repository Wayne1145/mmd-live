using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using MmdLive.Rig;
using MmdLive.Tracking;
using OpenCvSharp;

namespace MmdLive.Checks;

internal static class RetargetChecks
{
    static void Check(bool valid, string message)
    {
        if (!valid)
            throw new InvalidDataException(message);
    }

    public static void Contracts(AvatarRig rig)
    {
        var body = new Vector3[33];
        var visibility = new float[33];
        body[11] = new(400, 220, -900);
        body[12] = new(250, 220, -850);
        body[13] = new(410, 300, -900);
        body[14] = new(220, 300, -900);
        body[15] = new(450, 340, -1400);
        body[16] = new(225, 360, -1400);
        var world = new Vector3[33];
        world[11] = new(.18f, -.4f, -.5f);
        world[12] = new(-.18f, -.4f, -.5f);
        foreach (int i in Enumerable.Range(11, 6))
            visibility[i] = .99f;
        var points = Enumerable
            .Range(0, 21)
            .Select(i => new Vector3(326 + i % 4, 190 + i, -i))
            .ToArray();
        var sample = Motion.Empty with
        {
            Width = 640,
            Height = 480,
            Body = body,
            Visibility = visibility,
            WorldBody = world,
            Hands = [new(true, .99f, points)],
        };
        RigSnapshot Apply(Motion current, float alpha = 1)
        {
            var trace = new RigTrace();
            rig.Apply(current, false, alpha, trace);
            return rig.Snapshot(trace, current);
        }
        var snap = Apply(sample);
        var wrist = snap.Bones.Single(b => b.Name == "左手首");
        var modelWrist = snap.Alignment!.Value.Pixel(wrist.Start);
        Check(
            Vector2.Distance(modelWrist, new(points[0].X, points[0].Y)) < .5f,
            "camera-registered hand wrist must drive the arm even if pose wrist disagrees"
        );
        int spine = rig.Model.Bones.FindIndex(b => b.Name == "上半身");
        Check(
            Math.Abs(Vector3.TransformNormal(Vector3.UnitY, rig.Skin[spine]).Z) < .01f,
            "cropped half-body must keep its rest pitch"
        );
        var observedArms = Apply(sample with { Hands = [] });
        var elbow = observedArms.Bones.Single(b => b.Name == "左ひじ");
        var projected = observedArms.Alignment!.Value.Pixel(elbow.Start);
        Check(
            Vector2.Distance(projected, new(body[13].X, body[13].Y)) < .5f,
            "a feasible visible elbow must stay on its measured point when solving the wrist"
        );
        var partial = (Vector3[])points.Clone();
        partial[8] = new(1000, 800, 0);
        var clipped = Apply(sample with { Hands = [new(true, .99f, partial)] });
        Check(
            !clipped.Bones.Any(b =>
                b.Name.Normalize(System.Text.NormalizationForm.FormKC) == "左人指3"
            ) && clipped.Bones.Any(b => b.Name == "左手首"),
            "a clipped fingertip must not remove the observed wrist or draw the invisible segment"
        );
        Check(
            !Apply(sample with { Hands = [] }, .32f)
                .Bones.Any(b => b.Name.Contains("指") || b.Name.Contains("手首")),
            "dropping a hand clears every palm and finger driver immediately"
        );
        Check(
            Apply(Motion.Empty, .32f).Bones.Length == 0,
            "empty frame must clear active bones during relaxation"
        );
        LiveContinuity(rig, sample);
        long now = Stopwatch.GetTimestamp(),
            old = now - Stopwatch.Frequency;
        Check(
            new InputSample(sample, old).Expired(now)
                && !new InputSample(sample, old, true).Expired(now),
            "stalled live input expires while a still photograph remains valid"
        );
    }

    static void LiveContinuity(AvatarRig rig, Motion motion)
    {
        rig.ResetLiveTracking();
        int wrist = rig.Model.Bones.FindIndex(b => b.Name == "左手首");
        Quaternion Rotation() =>
            Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rig.Skin[wrist]));
        static float Angle(Quaternion a, Quaternion b) =>
            MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a, b)), 0, 1)) * 360 / MathF.PI;
        long tick = Stopwatch.Frequency / 30,
            stamp = tick;
        InputSample Frame(Motion m) => new(m, stamp += tick);
        var palm = (Vector3[])motion.Hands[0].Points.Clone();
        palm[0] = new(345, 240, 0);
        palm[9] = new(345, 204, -4);
        palm[5] = new(325, 205, -5);
        palm[17] = new(365, 205, -5);
        Motion WithPalm(Vector3[] p) => motion with { Hands = [new(true, 1, p)] };
        var open = Frame(WithPalm(palm));
        rig.ApplySample(open, false, stamp);
        var missing = Frame(motion with { Hands = [] });
        var trace = rig.ApplySample(missing, false, stamp);
        Check(!trace.Targets.ContainsKey(wrist), "first missing live sample still draws a palm");
        var reverse = (Vector3[])palm.Clone();
        (reverse[5], reverse[17]) = (reverse[17], reverse[5]);
        var turned = WithPalm(reverse);
        var reacquired = Frame(turned);
        var before = Rotation();
        rig.ApplySample(reacquired, false, stamp);
        var after = Rotation();
        float step = Angle(before, after);
        Check(
            step > 1 && step <= 22.05f,
            "reacquisition must turn towards an observed wrist within the 30 Hz allowance"
        );
        for (int i = 1; i <= 7; i++)
            rig.ApplySample(reacquired, false, stamp + tick * i / 8);
        float eased = Angle(before, Rotation());
        // Render frames between samples continue towards the same observation, bounded by the
        // 660°/s wrist allowance over the render time that actually passed.
        Check(eased > step, "render frames between camera samples did not ease the pose");
        Check(
            eased <= step + 660f * 7 / 8 / 30 + .05f,
            "re-rendering a sample banked extra rotation"
        );
        var skin = rig.Skin.ToArray();
        rig.ApplySample(reacquired, false, stamp + tick * 7 / 8);
        Check(skin.SequenceEqual(rig.Skin), "a render with no elapsed time advanced the pose");
        for (int i = 0; i < 18; i++)
            rig.ApplySample(Frame(turned), false, stamp);
        Check(
            Angle(before, Rotation()) > 100,
            "limiting the first turn froze a sustained real palm rotation"
        );
        Check(
            rig.ApplySample(InputSample.Empty, false, stamp + tick).Targets.Count == 0,
            "stale input kept live debug bones"
        );
        stamp += Stopwatch.Frequency * 3;
        rig.ApplySample(InputSample.Empty, false, stamp);
        var rest = Rotation();
        rig.ApplySample(Frame(turned), false, stamp);
        Check(Angle(rest, Rotation()) <= 33.05f, "a long gap banked unbounded rotation on return");
        rig.ResetLiveTracking();
    }

    // Encode the phase in a corner of a file source. Read it back from the exact bitmap delivered to the UI.
    public static async Task Occlusion(string model, string photo, string directory)
    {
        Directory.CreateDirectory(directory);
        using var still = Cv2.ImRead(photo);
        using var tracker = new MmdLive.Tracking.Tracker(
            Program.Models,
            Rendering.GraphicsAdapter.List().First().Index
        );
        var initial = tracker.Track(still);
        Check(
            initial.Face.Length == 478 && initial.Hands.Length == 2,
            "fixture needs a face and two hands"
        );
        var masks = new Vector3[][]
        {
            [],
            initial.Hands.Single(h => !h.Left).Points,
            initial.Hands.Single(h => h.Left).Points,
            initial.Face,
            [],
            [],
        };
        string file = Path.Combine(directory, "occlusion.avi");
        using (var writer = new VideoWriter(file, FourCC.MJPG, 12, still.Size()))
        {
            Check(writer.IsOpened(), "MJPEG writer unavailable");
            for (int i = 0; i < masks.Length; i++)
            {
                using var frame = still.Clone();
                var pts = masks[i];
                if (pts.Length > 0)
                {
                    int l = Math.Max(0, (int)pts.Min(p => p.X) - 9),
                        t = Math.Max(0, (int)pts.Min(p => p.Y) - 9),
                        r = Math.Min(frame.Width, (int)pts.Max(p => p.X) + 10),
                        b = Math.Min(frame.Height, (int)pts.Max(p => p.Y) + 10);
                    Cv2.Rectangle(frame, new Rect(l, t, r - l, b - t), Scalar.All(42), -1);
                }
                if (i == 4)
                    frame.SetTo(Scalar.All(45));
                Cv2.Rectangle(frame, new Rect(0, 0, 30, 30), Scalar.All(20 + i * 40), -1);
                for (int n = 0; n < 5; n++)
                    writer.Write(frame);
            }
        }
        var rig = new AvatarRig(model);
        using var input = new MotionInput(Rendering.GraphicsAdapter.List().First().Index)
        {
            PreviewVisible = true,
        };
        await input.Start(new(SourceKind.Video, file));
        var clock = Stopwatch.StartNew();
        var phases = new bool[masks.Length];
        var report = new List<object>();
        try
        {
            while (clock.Elapsed.TotalSeconds < 18 && !phases.All(seen => seen))
            {
                await Task.Delay(8);
                var sample = input.Read(out var image);
                using var pixels = image;
                if (pixels == null)
                    continue;
                int phase = (int)Math.Round((pixels.GetPixel(3, 3).R - 20) / 40.0);
                if ((uint)phase >= masks.Length)
                    continue;
                var m = sample.Detected;
                var trace = rig.ApplySample(sample, false, Stopwatch.GetTimestamp());
                var snapshot = rig.Snapshot(trace, m);
                bool left = m.Hands.Any(h => h.Left),
                    right = m.Hands.Any(h => !h.Left);
                bool HasHand(string side) =>
                    snapshot.Bones.Any(b =>
                        b.Name.StartsWith(side)
                        && (b.Name.Contains("指") || b.Name.Contains("手首"))
                    );
                if (phase == 0 || phase == 5)
                    Check(left && right && m.Face.Length == 478, "whole person/reacquisition");
                if (phase == 1)
                    Check(!right && left && !HasHand("右"), "first masked right-hand frame");
                if (phase == 2)
                    Check(!left && right && !HasHand("左"), "first masked left-hand frame");
                if (phase == 3)
                    Check(
                        m.Face.Length == 0
                            && !snapshot.Bones.Any(b => b.Name is "頭" or "左目" or "右目"),
                        "first masked face frame"
                    );
                if (phase == 4)
                    Check(
                        !left && !right && m.Face.Length == 0 && snapshot.Bones.Length == 0,
                        "empty frame contains ghost drivers"
                    );
                if (!phases[phase])
                    report.Add(
                        new
                        {
                            phase,
                            face = m.Face.Length,
                            left,
                            right,
                            bones = snapshot.Bones.Length,
                        }
                    );
                phases[phase] = true;
            }
        }
        finally
        {
            await input.Stop();
        }
        Check(phases.All(seen => seen), "missing video phase");
        File.WriteAllText(
            Path.Combine(directory, "result.json"),
            JsonSerializer.Serialize(report, Program.Json)
        );
        Console.WriteLine(
            "Verified live-input loss and reacquisition for face, left hand, right hand and empty frames."
        );
    }
}
