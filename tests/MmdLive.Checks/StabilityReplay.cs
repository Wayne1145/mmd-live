using System.Numerics;
using System.Text.Json;
using MmdLive.Rendering;
using MmdLive.Rig;
using MmdLive.Tracking;
using OpenCvSharp;

namespace MmdLive.Checks;

// File inputs only. Keep personal recordings, landmarks and model overlays outside Git.
internal static class StabilityReplay
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static readonly string[] Bones = ["頭", "左腕", "左ひじ", "左手首", "右腕", "右ひじ", "右手首"];

    internal static void Observe(string file, string output, int repeats)
    {
        using var tracker = new MmdLive.Tracking.Tracker(
            Program.Models,
            GraphicsAdapter.List().First().Index
        );
        using var writer = File.CreateText(output);
        if (repeats > 0)
        {
            using var still = Cv2.ImRead(file);
            if (still.Empty())
                throw new InvalidDataException("Unreadable photograph");
            for (int i = 0; i < repeats; i++)
                Write(still);
        }
        else
        {
            using var video = new VideoCapture(file, VideoCaptureAPIs.FFMPEG);
            using var image = new Mat();
            if (!video.IsOpened())
                throw new InvalidDataException("Unreadable video");
            while (video.Read(image) && !image.Empty())
                Write(image);
        }
        void Write(Mat image) =>
            writer.WriteLine(JsonSerializer.Serialize(tracker.Track(image), Json));
    }

    internal static void Run(
        string model,
        string observations,
        string output,
        string? clip,
        string? overlayList
    )
    {
        Directory.CreateDirectory(output);
        var rig = new AvatarRig(model);
        int[] joints = Bones
            .Select(name => rig.Model.Bones.FindIndex(b => b.Name == name))
            .ToArray();
        if (joints.Contains(-1))
            throw new InvalidDataException("Replay requires a standard MMD upper body");
        Quaternion Rotation(int i) =>
            Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rig.Skin[i]));
        Vector3 Position(int i) => Vector3.Transform(rig.Model.Bones[i].Position, rig.Skin[i]);
        static float Angle(Quaternion a, Quaternion b) =>
            360 / MathF.PI * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a, b)), 0, 1));
        static Vector2 XY(Vector3 p) => new(p.X, p.Y);
        using var video = clip == null ? null : new VideoCapture(clip, VideoCaptureAPIs.FFMPEG);
        var overlayFrames = overlayList?.Split(',').Select(int.Parse).ToHashSet();
        if (video != null && !video.IsOpened())
            throw new InvalidDataException("Unreadable overlay video");
        double fps = video?.Fps is > 0 and var rate ? rate : 30;
        using var image = new Mat();
        using var writer = File.CreateText(Path.Combine(output, "metrics.ndjson"));
        Quaternion[]? previous = null,
            previousLocal = null;
        int frame = 0;
        foreach (var line in File.ReadLines(observations))
        {
            Motion m = JsonSerializer.Deserialize<Motion>(line, Json)!;
            long stamp = (long)((frame + 1.0) * System.Diagnostics.Stopwatch.Frequency / fps);
            var sample = new InputSample(m, stamp);
            rig.ApplySample(sample, false, stamp);
            var trace = rig.ApplySample(
                sample,
                false,
                stamp + System.Diagnostics.Stopwatch.Frequency / 60
            );
            var snapshot = rig.Snapshot(trace, m);
            var global = joints.Select(Rotation).ToArray();
            var local = joints
                .Select(
                    (joint, i) =>
                        rig.Model.Bones[joint].ParentIndex is >= 0 and var parent
                            ? Quaternion.Inverse(Rotation(parent)) * global[i]
                            : global[i]
                )
                .ToArray();
            var worldStep =
                previous == null ? null : global.Select((q, i) => Angle(q, previous[i])).ToArray();
            var localStep =
                previousLocal == null
                    ? null
                    : local.Select((q, i) => Angle(q, previousLocal[i])).ToArray();
            var sides = new List<object>();
            for (int s = 0; s < 2; s++)
            {
                var hand = m.Hands.FirstOrDefault(h => h.Left == (s == 0));
                int j = 1 + s * 3;
                var shoulder = Position(joints[j]);
                var elbow = Position(joints[j + 1]);
                var wrist = Position(joints[j + 2]);
                sides.Add(
                    new
                    {
                        hand = hand != null,
                        elbowSeen = m.Sees(13 + s),
                        wristSeen = m.Sees(15 + s),
                        rawWrist = hand == null ? (Vector2?)null : XY(hand.Points[0]),
                        upperDepth = elbow.Z - shoulder.Z,
                        lowerDepth = wrist.Z - elbow.Z,
                        wristErrorPct = hand != null && snapshot.Alignment is { } a
                            ? (float?)(
                                100 * Vector2.Distance(a.Pixel(wrist), XY(hand.Points[0])) / m.Width
                            )
                            : null,
                        rigWrist = snapshot.Alignment?.Pixel(wrist),
                    }
                );
            }
            writer.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        frame,
                        worldStep,
                        localStep,
                        sides,
                        face = m.Face.Length > 0,
                        body = m.Body.Length > 0,
                    },
                    Json
                )
            );
            if (video != null)
            {
                if (!video.Read(image) || image.Empty())
                    throw new InvalidDataException("Video/observation length mismatch");
                if (overlayFrames?.Contains(frame) ?? (frame % 15 == 0))
                    ObservedSkeleton.Save(
                        image,
                        Path.Combine(output, $"overlay-{frame:D3}.png"),
                        m,
                        snapshot,
                        false
                    );
            }
            previous = global;
            previousLocal = local;
            frame++;
        }
        if (frame < 2)
            throw new InvalidDataException("Need at least two observations");
        Console.WriteLine($"{frame} frames. Joint order {string.Join(", ", Bones)}");
    }
}
