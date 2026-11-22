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
    bool fullBody;
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

    public void SetPose(RigSnapshot snapshot, Matrix4x4 viewProjection, bool full)
    {
        pose = snapshot;
        projection = viewProjection;
        fullBody = full;
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
                "在左侧选择输入源，然后点击「开始」",
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
        ObservedSkeleton.Draw(g, m, box, fullBody, rawPen, rawDot);
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
        string caption = BoneUnderPointer()
            ?? (
                pose?.Bones.Length > 0
                    ? $"{aligned}   |   指向橙色骨节查看模型骨名"
                    : "当前帧没有可驱动的骨骼"
            );
        Label(g, caption, new(8, Height - footer, Width - 12, footer), ForeColor);
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
