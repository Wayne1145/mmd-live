using System.Diagnostics;
using System.Text.Json;
using MmdLive.Rendering;
using MmdLive.Rig;
using MmdLive.Tracking;
using OpenCvSharp;

namespace MmdLive;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = true,
    };
    internal static string Models => Path.Combine(AppContext.BaseDirectory, "models");
    internal static string Demo =>
        Path.Combine(AppContext.BaseDirectory, "studio", "StudioPilot.pmx");

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 0 || args[0] == "--studio")
        {
            try
            {
                Application.Run(new StudioForm(args.Skip(1).ToArray()));
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "MMD Live", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        var output = Array.IndexOf(args, "--out");
        if (output < 0 || output + 1 >= args.Length)
            return 2;
        string directory = Path.GetFullPath(args[output + 1]);
        Directory.CreateDirectory(directory);
        string? Value(string key)
        {
            int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        try
        {
            var adapters = GraphicsAdapter.List();
            int selected = Value("--adapter") is { } index
                ? int.Parse(index)
                : adapters.First().Index;
            var avatar = new AvatarRig(Value("--model") ?? Demo);
            var size = Value("--size") == "1080" ? (1920, 1080) : (960, 540);
            using var render = new Renderer(0, selected, size.Item1, size.Item2)
            {
                FullBody = args.Contains("--full"),
            };
            render.Load(avatar);
            render.Render();
            render.SavePng(Path.Combine(directory, "neutral.png"));
            using var tracker = new Tracking.Tracker(Models, selected);
            using var frame = Cv2.ImRead(
                Value("--image") ?? throw new ArgumentException("--image 需要图片路径")
            );
            if (frame.Empty())
                throw new InvalidDataException("无法读取图片");
            int repeat = Math.Clamp(int.Parse(Value("--repeat") ?? "1"), 1, 1000000);
            var timings = new double[repeat];
            Motion motion = Motion.Empty;
            for (int i = 0; i < repeat; i++)
            {
                motion = tracker.Track(frame);
                avatar.Apply(motion, render.FullBody, 1);
                render.Render();
                timings[i] = motion.Milliseconds;
            }
            render.SavePng(Path.Combine(directory, "avatar.png"));
            DrawLandmarks(frame, motion);
            Cv2.ImWrite(Path.Combine(directory, "landmarks.jpg"), frame);
            File.WriteAllText(
                Path.Combine(directory, "motion.json"),
                JsonSerializer.Serialize(motion, Json)
            );
            var report = new
            {
                Adapter = adapters.Single(a => a.Index == selected).Name,
                Backend = "DirectML",
                CpuFallback = false,
                Avatar = avatar.Model.Name,
                Vertices = avatar.Model.Vertices.Length,
                Bones = avatar.Model.Bones.Count,
                Morphs = avatar.Model.Morphs.Count,
                Face = motion.Face.Length,
                Pose = motion.Body.Length,
                Hands = motion.Hands.Select(h => new
                {
                    h.Left,
                    Landmarks = h.Points.Length,
                    h.Confidence,
                }),
                timings,
            };
            File.WriteAllText(
                Path.Combine(directory, "report.json"),
                JsonSerializer.Serialize(report, Json)
            );
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
            return 1;
        }
    }

    internal static void DrawLandmarks(Mat frame, Motion m)
    {
        void Dot(System.Numerics.Vector3 p, Scalar c, int r) =>
            Cv2.Circle(
                frame,
                new OpenCvSharp.Point((int)p.X, (int)p.Y),
                r,
                c,
                -1,
                LineTypes.AntiAlias
            );
        foreach (var p in m.Face)
            Dot(p, new Scalar(170, 195, 55), 1);
        for (int i = 0; i < m.Body.Length; i++)
            if (m.Visibility[i] > .45f)
                Dot(m.Body[i], new Scalar(80, 170, 255), 3);
        foreach (var hand in m.Hands)
        foreach (var p in hand.Points)
            Dot(p, hand.Left ? new Scalar(150, 240, 40) : new Scalar(240, 100, 180), 3);
    }
}
