using System.Diagnostics;
using System.Drawing.Imaging;
using System.Numerics;
using MmdLive.Rig;
using MmdLive.Tracking;

namespace MmdLive.Checks;

internal static class PreviewChecks
{
    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    internal static void Contracts(AvatarRig rig, Motion face)
    {
        using var box = new PreviewMailbox();
        var first = new InputSample(face, 1);
        var second = new InputSample(Motion.Empty, 2);
        box.Publish(first, new Bitmap(8, 8));
        var secondPixels = new Bitmap(12, 12);
        box.Publish(second, secondPixels);
        Require(
            ReferenceEquals(box.Read(out var image), second)
                && ReferenceEquals(image, secondPixels),
            "image and current pose must travel together"
        );
        image!.Dispose();
        box.Read(out var consumed);
        Require(consumed == null, "a preview bitmap must only be transferred once");
        box.Publish(InputSample.Empty, null);
        Require(box.Latest == InputSample.Empty, "empty input clears pending samples");
        var rect = TrackingPreview.Fit(new(0, 0, 100, 100), 100, 50);
        Require(
            rect == new RectangleF(0, 25, 100, 50),
            "letterbox must preserve source coordinate space"
        );

        RigSnapshot Pose(Motion sample, bool full = false)
        {
            var trace = new RigTrace();
            rig.Apply(sample, full, 1, trace);
            return rig.Snapshot(trace, sample);
        }
        Require(
            Pose(Motion.Empty).Bones.Length == 0,
            "rest and inherited bones must not be reported as driven"
        );
        Require(
            Pose(face).Bones.Select(b => b.Name).ToHashSet().SetEquals(["頭", "左目", "右目"]),
            "face-only pose reports head and eyes"
        );
        var body = new Vector3[33];
        var visible = new float[33];
        body[11] = new(154, 65, 0);
        body[12] = new(54, 65, 0);
        body[13] = new(177, 85, 0);
        body[14] = new(40, 84, 0);
        body[23] = new(134, 140, 0);
        body[24] = new(74, 140, 0);
        body[25] = new(142, 183, 0);
        foreach (int i in new[] { 11, 12, 13, 14, 23, 24, 25 })
            visible[i] = 1;
        var sample = Motion.Empty with
        {
            Body = body,
            Visibility = visible,
            Width = 210,
            Height = 200,
        };
        var half = Pose(sample);
        var left = half.Bones.Single(b => b.Name == "左腕");
        var right = half.Bones.Single(b => b.Name == "右腕");
        var center =
            (half.Alignment!.Value.Pixel(left.Start) + half.Alignment.Value.Pixel(right.Start)) / 2;
        Require(
            Vector2.Distance(center, new(104, 65)) < .01f
                && !half.Bones.Any(b => b.Name == "左足")
                && Pose(sample, true).Bones.Any(b => b.Name == "左足"),
            "registration and half/full drive gating"
        );
        var mapped = rig.Model.Bones.First(b => b.Name == "左ひじ");
        rig.Bind(new() { { "左腕", mapped.Name } }, new());
        Require(
            !Pose(sample).Bones.Any(b => b.Name == "左腕"),
            "show the resolved binding, not a canonical bone name"
        );
        rig.Bind(new(), new());
    }

    // Exercises the same tab, checkbox and stop button that a camera user operates, using a file source.
    internal static void Studio(string model, string source, string directory, bool video)
    {
        Application.EnableVisualStyles();
        Directory.CreateDirectory(directory);
        using var studio = new StudioForm([
            "--model",
            model,
            video ? "--video" : "--image",
            source,
            "--settings-dir",
            Path.Combine(directory, "profiles"),
        ]);
        static IEnumerable<Control> Children(Control root) =>
            root.Controls.Cast<Control>().SelectMany(c => Children(c).Prepend(c));
        var controls = Children(studio).ToArray();
        var preview = controls.OfType<TrackingPreview>().Single();
        var check = controls.OfType<CheckBox>().Single(c => c.Text == "显示骨骼");
        // One button starts and stops the selected source; while input runs it reads "停止".
        var stop = controls.OfType<Button>().Single(c => c.AccessibleName == "开始或停止输入");
        var timer = new System.Windows.Forms.Timer { Interval = 140 };
        var clock = Stopwatch.StartNew();
        Exception? error = null;
        int stage = 0;
        long last = 0,
            firstStamp = 0;
        using var original = new MemoryStream();
        Bitmap Grab(Control c)
        {
            var b = new Bitmap(c.Width, c.Height);
            c.DrawToBitmap(b, c.ClientRectangle);
            return b;
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                if (clock.Elapsed.TotalSeconds > 45)
                    throw new TimeoutException($"preview stage {stage}");
                if (clock.ElapsedMilliseconds - last < 350)
                    return;
                if (stage == 0 && preview.Sample.Detected.Face.Length == 478)
                {
                    Require(preview.Visible, "source start must show the preview");
                    Require(stop.Text.Contains("停止"), "a running source must offer to stop");
                    check.Checked = false;
                    firstStamp = preview.Sample.Stamp;
                    using var b = Grab(preview);
                    b.Save(original, ImageFormat.Png);
                    b.Save(Path.Combine(directory, "raw.png"));
                    check.Checked = true;
                    stage++;
                }
                else if (stage == 1)
                {
                    using var b = Grab(preview);
                    int turquoise = 0,
                        orange = 0,
                        rigOrange = 0;
                    for (int y = 32; y < b.Height - 32; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        var p = b.GetPixel(x, y);
                        if (p.R == 59 && p.G == 237 && p.B == 215)
                            turquoise++;
                        if (p.R == 255 && p.G == 176 && p.B == 66)
                        {
                            if (x < b.Width * .62f)
                                orange++;
                            else
                                rigOrange++;
                        }
                    }
                    Require(
                        turquoise > 8 && orange > 8 && rigOrange > 8,
                        $"real landmarks, registered driven bones and model overlay: {turquoise}, {orange}, {rigOrange}"
                    );
                    var names = controls
                        .OfType<TextBox>()
                        .Single(c => c.AccessibleName == "本帧驱动骨名");
                    Require(
                        names.Visible && names.Text.Length > 0,
                        "bound bone names must be visible"
                    );
                    b.Save(Path.Combine(directory, "bones.png"));
                    using var whole = Grab(studio);
                    whole.Save(Path.Combine(directory, "console.png"));
                    check.Checked = false;
                    stage++;
                }
                else if (stage == 2)
                {
                    using var b = Grab(preview);
                    using var stream = new MemoryStream();
                    b.Save(stream, ImageFormat.Png);
                    bool identical = original.ToArray().SequenceEqual(stream.ToArray());
                    if (video)
                        Require(
                            !identical && preview.Sample.Stamp > firstStamp,
                            "live input advances to new pixels and a new capture timestamp"
                        );
                    else
                        Require(identical, "turning the overlay off restores unannotated pixels");
                    stop.PerformClick();
                    stage++;
                }
                else if (stage == 3 && preview.Sample.Stamp == 0)
                {
                    Require(!check.Checked, "checkbox state");
                    Require(stop.Text.Contains("开始"), "a stopped source must offer to start again");
                    studio.Close();
                }
                else
                    return;
                last = clock.ElapsedMilliseconds;
            }
            catch (Exception e)
            {
                error = e;
                studio.Close();
            }
        };
        timer.Start();
        try
        {
            Application.Run(studio);
        }
        finally
        {
            timer.Dispose();
        }
        if (error != null)
            throw new InvalidDataException("Studio preview regression", error);
        Require(stage == 3, "interactive preview did not run to completion");
        Console.WriteLine("Studio input preview, both overlays, checkbox and stop passed.");
    }
}
