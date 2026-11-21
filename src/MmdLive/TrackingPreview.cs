using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using MmdLive.Rig;
using MmdLive.Tracking;

namespace MmdLive;

// This control lives in the console. The OBS swap chain never receives input pixels or debug drawing.
internal sealed class TrackingPreview : Control
{
    static readonly Color RawColor = Color.FromArgb(59, 237, 215),
        RigColor = Color.FromArgb(255, 176, 66);
    static readonly int[][] BodyPaths =
    [
        [11, 12, 24, 23, 11],
        [11, 13, 15],
        [12, 14, 16],
        [23, 25, 27, 29, 31, 27],
        [24, 26, 28, 30, 32, 28],
    ];
    static readonly int[][] FingerPaths =
    [
        [0, 1, 2, 3, 4],
        [0, 5, 6, 7, 8],
        [5, 9, 10, 11, 12],
        [9, 13, 14, 15, 16],
        [13, 17, 18, 19, 20],
        [0, 17],
    ];
    static readonly int[][] FacePaths =
    [
        [10, 338, 284, 389, 454, 397, 378, 152, 149, 172, 234, 162, 54, 109, 10],
        [33, 160, 158, 133, 153, 144, 33],
        [362, 385, 387, 263, 373, 380, 362],
        [61, 40, 37, 0, 267, 270, 291, 321, 314, 17, 84, 91, 61],
        [78, 13, 308, 14, 78],
        [168, 1, 2],
    ];
    readonly Pen rawPen = new(RawColor, 1.65f),
        rigPen = new(RigColor, 2f);
    readonly SolidBrush rawDot = new(RawColor),
        rigDot = new(RigColor);
    readonly SolidBrush panel = new(Color.FromArgb(37, 45, 48));
    readonly List<(PointF Point, string Name)> hits = [];
    Bitmap? inputImage,
        avatar;
    RigSnapshot? pose;
    Matrix4x4 projection;
    Point? pointer;
    bool showBones;

    [System.ComponentModel.DefaultValue(false)]
    internal bool ShowBones
    {
        get => showBones;
        set
        {
            showBones = value;
            pose = null;
            pointer = null;
            hits.Clear();
            Invalidate();
        }
    }
    public InputSample Sample { get; private set; } = InputSample.Empty;

    public TrackingPreview()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(23, 30, 33);
        ForeColor = Color.WhiteSmoke;
        DoubleBuffered = true;
        ResizeRedraw = true;
        AccessibleName = "追踪预览画面";
    }

    public void SetInput(InputSample sample, Bitmap bitmap)
    {
        inputImage?.Dispose();
        inputImage = bitmap;
        Sample = sample;
        pose = null;
        Invalidate();
    }

    public void ClearFrame()
    {
        if (inputImage == null)
            return;
        inputImage.Dispose();
        inputImage = null;
        Sample = InputSample.Empty;
        pose = null;
        pointer = null;
        hits.Clear();
        Invalidate();
    }

    public Bitmap AvatarBuffer(Size size)
    {
        if (avatar?.Size != size)
        {
            avatar?.Dispose();
            avatar = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        }
        return avatar;
    }

    public void SetPose(RigSnapshot snapshot, Matrix4x4 viewProjection)
    {
        pose = snapshot;
        projection = viewProjection;
        Invalidate();
    }

    internal static RectangleF Fit(RectangleF bounds, int w, int h)
    {
        float scale = Math.Min(bounds.Width / w, bounds.Height / h);
        return new(
            bounds.X + (bounds.Width - w * scale) / 2,
            bounds.Y + (bounds.Height - h * scale) / 2,
            w * scale,
            h * scale
        );
    }

    static PointF Pixel(RectangleF box, float x, float y, int w, int h) =>
        new(box.X + box.Width * x / w, box.Y + box.Height * y / h);

    const TextFormatFlags TextFlags =
        TextFormatFlags.VerticalCenter
        | TextFormatFlags.EndEllipsis
        | TextFormatFlags.SingleLine
        | TextFormatFlags.NoPrefix;

    void Label(
        Graphics g,
        string text,
        RectangleF box,
        Color color,
        TextFormatFlags flags = TextFlags
    ) => TextRenderer.DrawText(g, text, Font, Rectangle.Round(box), color, flags);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        hits.Clear();
        if (inputImage == null)
        {
            Label(
                g,
                "等待摄像头、图片或视频输入",
                ClientRectangle,
                ForeColor,
                TextFlags | TextFormatFlags.HorizontalCenter
            );
            return;
        }
        bool split = ShowBones;
        const int header = 29,
            footer = 31,
            gap = 5;
        int leftWidth = split ? (int)(ClientSize.Width * .62f) : ClientSize.Width;
        RectangleF content = new(
            0,
            header,
            leftWidth,
            Math.Max(1, Height - header - (split ? footer : 0))
        );
        var box = Fit(content, inputImage.Width, inputImage.Height);
        var m = Sample.Detected;
        Label(
            g,
            $"输入画面   {m.Width} × {m.Height}",
            new(8, 0, leftWidth - 12, header),
            ForeColor
        );
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(inputImage, box);
        if (!split)
            return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var save = g.Save();
        g.SetClip(box);
        DrawDetected(g, m, box);
        if (pose?.Alignment is { } align)
            DrawRig(g, box, p =>
            {
                var xy = align.Pixel(p);
                return Pixel(box, xy.X, xy.Y, m.Width, m.Height);
            });
        g.Restore(save);
        var right = new RectangleF(
            leftWidth + gap,
            header,
            Math.Max(1, Width - leftWidth - gap),
            content.Height
        );
        g.FillRectangle(panel, right);
        Label(
            g,
            $"角色    {pose?.Bones.Length ?? 0} 根驱动骨骼",
            new(right.X + 4, 0, right.Width - 6, header),
            RigColor
        );
        if (avatar != null && pose != null)
        {
            // Center-crop the wide broadcast frame to make the model readable in the comparison pane.
            float cropWidth = Math.Min(
                avatar.Width,
                Math.Max(avatar.Width * .6f, avatar.Height * right.Width / right.Height)
            );
            var src = new RectangleF((avatar.Width - cropWidth) / 2, 0, cropWidth, avatar.Height);
            var dest = Fit(right, (int)cropWidth, avatar.Height);
            g.DrawImage(avatar, dest, src, GraphicsUnit.Pixel);
            save = g.Save();
            g.SetClip(dest);
            DrawRig(g, dest, p =>
            {
                var v = Vector4.Transform(new Vector4(p, 1), projection);
                return new PointF(
                    dest.X
                        + ((v.X / v.W + 1) * .5f * avatar.Width - src.X) / src.Width * dest.Width,
                    dest.Y + (.5f - v.Y / v.W * .5f) * dest.Height
                );
            });
            g.Restore(save);
        }
        string aligned = pose?.Alignment is { } a ? $"按{a.Anchor}对齐" : "等待对齐点";
        string held = HeldChannels(Sample);
        string caption = BoneUnderPointer()
            ?? (
                held.Length > 0
                    ? $"暂持上一姿势 · {held}   |   {aligned}"
                    : $"{aligned}   |   指向橙色骨节查看模型骨名"
            );
        Label(g, caption, new(8, Height - footer, Width - 12, footer), ForeColor);
    }

    static string HeldChannels(InputSample s)
    {
        var missing = new List<string>(4);
        if (s.Detected.Face.Length == 0 && s.Driven.Face.Length > 0)
            missing.Add("脸");
        if (s.Detected.Body.Length == 0 && s.Driven.Body.Length > 0)
            missing.Add("身体");
        foreach (var hand in s.Driven.Hands)
            if (!s.Detected.Hands.Any(h => h.Left == hand.Left))
                missing.Add(hand.Left ? "左手" : "右手");
        return string.Join("、", missing);
    }

    void DrawDetected(Graphics g, Motion m, RectangleF box)
    {
        PointF Map(Vector3 p) => Pixel(box, p.X, p.Y, m.Width, m.Height);
        void Dot(Vector3 p, float r)
        {
            var a = Map(p);
            g.FillEllipse(rawDot, a.X - r, a.Y - r, r * 2, r * 2);
        }
        void Paths(Vector3[] points, int[][] paths, float[]? vis = null)
        {
            foreach (var path in paths)
                for (int i = 1; i < path.Length; i++)
                {
                    int a = path[i - 1],
                        b = path[i];
                    if (vis != null && (vis[a] < .4f || vis[b] < .4f))
                        continue;
                    g.DrawLine(rawPen, Map(points[a]), Map(points[b]));
                }
        }
        void Bounds(Vector3[] points)
        {
            var min = new Vector2(float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity);
            foreach (var p in points)
            {
                var xy = new Vector2(p.X, p.Y);
                min = Vector2.Min(min, xy);
                max = Vector2.Max(max, xy);
            }
            var a = Pixel(box, min.X, min.Y, m.Width, m.Height);
            var b = Pixel(box, max.X, max.Y, m.Width, m.Height);
            using var border = new Pen(Color.FromArgb(135, RawColor), 1)
            {
                DashStyle = DashStyle.Dot,
            };
            g.DrawRectangle(border, a.X - 2, a.Y - 2, b.X - a.X + 4, b.Y - a.Y + 4);
        }
        if (m.Body.Length == 33)
        {
            Paths(m.Body, BodyPaths, m.Visibility);
            for (int i = 11; i < 33; i++)
                if (m.Visibility[i] >= .4f)
                    Dot(m.Body[i], 2.25f);
        }
        if (m.Face.Length == 478)
        {
            Paths(m.Face, FacePaths);
            Dot(m.Face[468], 2);
            Dot(m.Face[473], 2);
            Bounds(m.Face);
        }
        foreach (var h in m.Hands)
        {
            Paths(h.Points, FingerPaths);
            foreach (var p in h.Points)
                Dot(p, 1.7f);
            Bounds(h.Points);
        }
    }

    void DrawRig(Graphics g, RectangleF bounds, Func<Vector3, PointF> map)
    {
        if (pose == null)
            return;
        foreach (var bone in pose.Bones)
        {
            var a = map(bone.Start);
            var b = map(bone.End);
            if (!float.IsFinite(a.X + a.Y + b.X + b.Y))
                continue;
            g.DrawLine(rigPen, a, b);
            g.FillEllipse(rigDot, a.X - 2.2f, a.Y - 2.2f, 4.4f, 4.4f);
            if (bounds.Contains(a))
                hits.Add((a, bone.Name));
        }
    }

    string? BoneUnderPointer()
    {
        if (pointer is not { } cursor)
            return null;
        string? closest = null;
        float nearest = 11 * 11;
        foreach (var (point, name) in hits)
        {
            float dx = cursor.X - point.X,
                dy = cursor.Y - point.Y;
            float distance = dx * dx + dy * dy;
            if (distance < nearest)
            {
                nearest = distance;
                closest = name;
            }
        }
        return closest;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!ShowBones || pointer == e.Location)
            return;
        pointer = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        pointer = null;
        if (ShowBones)
            Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inputImage?.Dispose();
            avatar?.Dispose();
            rawPen.Dispose();
            rigPen.Dispose();
            rawDot.Dispose();
            rigDot.Dispose();
            panel.Dispose();
        }
        base.Dispose(disposing);
    }
}
