using System.Numerics;
using System.Text.Json;
using MmdLive.Rendering;
using MmdLive.Rig;
using OpenCvSharp;

namespace MmdLive.Checks;

internal static class Check
{
    static void Require(bool ok, string message)
    {
        if (!ok)
            throw new InvalidDataException(message);
    }

    [STAThread]
    static void Main(string[] args)
    {
        switch (args[0])
        {
            case "contracts":
                Contracts(args[1]);
                break;
            case "preview":
                PreviewChecks.Studio(args[1], args[2], args[3], args.Contains("--video"));
                break;
            case "upright":
                UprightPlayback(args[1], args[2]);
                break;
            case "video":
                using (var input = new VideoCapture(args[1], VideoCaptureAPIs.FFMPEG))
                using (
                    var output = new VideoWriter(
                        args[2],
                        FourCC.MJPG,
                        30,
                        new OpenCvSharp.Size(2560, 1440)
                    )
                )
                {
                    Require(input.IsOpened() && output.IsOpened(), "video codec unavailable");
                    using var source = new Mat();
                    using var scaled = new Mat();
                    int frames = 0;
                    while (input.Read(source) && !source.Empty())
                    {
                        Cv2.Resize(source, scaled, new OpenCvSharp.Size(2560, 1440));
                        output.Write(scaled);
                        frames++;
                    }
                    Require(frames >= 10, "empty video");
                    Console.WriteLine($"{frames} frames, 2560×1440 at 30 FPS");
                }
                break;
            case "alpha":
                Alpha(args[1]);
                break;
            case "blank":
                using (var blank = new Mat(540, 960, MatType.CV_8UC3, Scalar.All(128)))
                    Cv2.ImWrite(args[1], blank);
                break;
            case "light":
                var rig = new AvatarRig(args[1]);
                using (var render = new Renderer(0, GraphicsAdapter.List().First().Index, 960, 540))
                {
                    render.Load(rig);
                    render.Render();
                    render.SavePng(Path.Combine(args[2], "lit.png"));
                    render.Lights.Key = render.Lights.Key with { Power = 0 };
                    render.Lights.Fill = render.Lights.Fill with { Power = 0 };
                    render.Lights.Rim = render.Lights.Rim with { Power = 0 };
                    render.Render();
                    render.SavePng(Path.Combine(args[2], "unlit.png"));
                    using var a = Cv2.ImRead(
                        Path.Combine(args[2], "lit.png"),
                        ImreadModes.Unchanged
                    );
                    using var b = Cv2.ImRead(
                        Path.Combine(args[2], "unlit.png"),
                        ImreadModes.Unchanged
                    );
                    using var diff = new Mat();
                    Cv2.Absdiff(a, b, diff);
                    var mean = Cv2.Mean(diff);
                    Require(
                        mean.Val0 + mean.Val1 + mean.Val2 > 5 && mean.Val3 < .05,
                        "lighting must change RGB and preserve alpha"
                    );
                    Console.WriteLine(mean);
                }
                break;
            default:
                throw new ArgumentException("contracts, upright, video, alpha, blank or light");
        }
    }

    static void Contracts(string source)
    {
        var character = new AvatarRig(source);
        Require(
            character.Model.Bones.Count == 50 && character.Model.Vertices.Length > 10000,
            "Studio Pilot import"
        );
        Require(
            character.Skin.All(x => Math.Abs(x.M11 - 1) < .00001f && Math.Abs(x.M41) < .0001f),
            "neutral skinning"
        );
        var requested = new CameraMode(2560, 1440, 30);
        Require(
            CameraMode.Prefer([new(1920, 1080, 60), new(2560, 1440, 15), requested]) == requested,
            "1440p native mode selection"
        );
        Require(
            CameraMode.Prefer([new(1280, 720, 30), new(1920, 1080, 30)])?.Width == 1920,
            "camera mode fallback"
        );
        var expression = new float[52];
        expression[9] = 1;
        expression[25] = .8f;
        var face = new System.Numerics.Vector3[478];
        face[263] = new(120, 60, 0);
        face[33] = new(80, 60, 0);
        face[152] = new(100, 100, 0);
        face[10] = new(100, 40, 0);
        var motion = Tracking.Motion.Empty with
        {
            Face = face,
            Expressions = expression,
            Width = 200,
            Height = 160,
        };
        character.Apply(motion, false, 1);
        float Weight(string name) =>
            character.EffectiveWeights[character.Model.Morphs.FindIndex(m => m.Name == name)];
        Require(
            Weight("ウィンク") > .9f && Weight("ウィンク右") == 0 && Weight("あ") > .7f,
            "anatomical left blink and jaw mapping"
        );
        var points = Enumerable.Repeat(new System.Numerics.Vector3(50, 40, 0), 21).ToArray();
        var right = new Tracking.Tracker.HandCandidate(.97f, .99f, points);
        var left = right with { RightProbability = .02f };
        Require(
            !Tracking.Tracker.AssignHands([right], [], []).Single().Left,
            "right hand outside body frame"
        );
        Require(
            Tracking.Tracker.AssignHands([left], [], []).Single().Left,
            "left hand outside body frame"
        );
        var duplicate = right with { Presence = .8f };
        Require(
            !Tracking.Tracker.AssignHands([right, duplicate], [], []).Single().Left,
            "two observations of the same wrist"
        );
        Require(
            Tracking
                .Tracker.AssignHands([left, right], [], [])
                .Select(h => h.Left)
                .Distinct()
                .Count() == 2,
            "two independent hands"
        );
        var body = new System.Numerics.Vector3[33];
        var visibility = new float[33];
        body[15] = points[0];
        visibility[15] = .9f;
        Require(
            Tracking.Tracker.AssignHands([right], body, visibility).Single().Left,
            "visible left wrist corrects a contradictory classifier"
        );
        body[15] += new System.Numerics.Vector3(200, 0, 0);
        Require(
            !Tracking.Tracker.AssignHands([right], body, visibility).Single().Left,
            "distant wrists preserve the hand classifier"
        );
        CheckHeadAndSpine(character, motion);
        PreviewChecks.Contracts(character, motion);
        character.Calibrate(motion);
        character.Apply(motion, false, 1);
        Require(Weight("ウィンク") == 0 && Weight("あ") == 0, "neutral calibration");
        Console.WriteLine(
            "PMX import, neutral skin, facial morphs, hand sides, calibration and camera modes passed."
        );
    }

    static void CheckHeadAndSpine(AvatarRig rig, Tracking.Motion motion)
    {
        var body = new Vector3[33];
        body[11] = body[12] = new(0, -600, -650);
        var world = new Vector3[33];
        world[11] = world[12] = new(0, -.4f, 0);
        var sample = motion with
        {
            Body = body,
            WorldBody = world,
            Visibility = Enumerable.Repeat(1f, 33).ToArray(),
        };
        int spine = rig.Model.Bones.FindIndex(b => b.Name == "上半身");
        int head = rig.Model.Bones.FindIndex(b => b.Name == "頭");
        rig.Apply(sample, false, 1);
        Require(
            Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, rig.Skin[spine]), Vector3.UnitY)
                < .001f,
            "projected hip depth must not bow an upright metric spine"
        );
        world[11] = world[12] = new(0, -.4f, -.25f);
        var face = (Vector3[])sample.Face.Clone();
        face[152].Z = (face[152].Y - face[10].Y) / 2;
        rig.Apply(sample with { Face = face }, false, 1);
        var forward = Vector3.TransformNormal(-Vector3.UnitZ, rig.Skin[head]);
        Require(
            Vector3.Distance(forward, Vector3.Normalize(new(0, -.5f, -1))) < .001f,
            "the measured head nod must remain in camera space above a bent spine"
        );
    }

    static void UprightPlayback(string video, string models)
    {
        using var input = new VideoCapture(video, VideoCaptureAPIs.FFMPEG);
        using var frame = new Mat();
        using var tracker = new Tracking.Tracker(models, GraphicsAdapter.List().First().Index);
        var rig = new AvatarRig(Program.Demo);
        int head = rig.Model.Bones.FindIndex(b => b.Name == "頭");
        for (int i = 0; i < 24; i++)
        {
            Require(input.Read(frame) && !frame.Empty(), "missing upright clip frame");
            var sample = tracker.Track(frame);
            Require(
                sample.Face.Length == 478 && sample.WorldBody.Length == 33,
                $"upright landmarks at frame {i}"
            );
            var offset = sample.Face[1] - sample.Body[0];
            Require(
                new Vector2(offset.X, offset.Y).Length() < frame.Width * .055f,
                $"face outside head at frame {i}"
            );
            rig.Apply(sample, false, 1);
            var forward = Vector3.TransformNormal(-Vector3.UnitZ, rig.Skin[head]);
            Require(-forward.Z > .85f, $"upright actor faces away from the camera at frame {i}");
        }
        Console.WriteLine(
            "Upright face acquisition, detector refresh and head alignment passed on 24 video frames."
        );
    }

    static void Alpha(string dir)
    {
        using var raw = Cv2.ImRead(Path.Combine(dir, "captured.png"), ImreadModes.Unchanged);
        using var black = Cv2.ImRead(Path.Combine(dir, "black.png"));
        using var white = Cv2.ImRead(Path.Combine(dir, "white.png"));
        Require(raw.Channels() == 4, "OBS screenshot lacks RGBA");
        int clear = 0,
            opaque = 0,
            partial = 0,
            bad = 0;
        int height = raw.Height,
            width = raw.Width;
        for (int y = 0; y < height; y += 2)
        for (int x = 0; x < width; x += 2)
        {
            int alpha = raw.At<Vec4b>(y, x).Item3;
            var b = black.At<Vec3b>(y, x);
            var w = white.At<Vec3b>(y, x);
            if (alpha == 0)
            {
                clear++;
                if (b.Item0 > 3 || w.Item0 < 252)
                    bad++;
            }
            else if (alpha == 255)
            {
                opaque++;
                if (Math.Abs(b.Item0 - w.Item0) > 6)
                    bad++;
            }
            else
                partial++;
        }
        Require(
            clear > 500 && opaque > 500 && partial > 0 && bad < (clear + opaque) * .012,
            "OBS compositing failed"
        );
        var result = new
        {
            clear,
            opaque,
            partial,
            bad,
        };
        var json = JsonSerializer.Serialize(result);
        File.WriteAllText(Path.Combine(dir, "pixels.json"), json);
        Console.WriteLine(json);
    }
}
