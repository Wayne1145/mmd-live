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

    static void Main(string[] args)
    {
        switch (args[0])
        {
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
                throw new ArgumentException("video, alpha, blank or light");
        }
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
