using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using MmdLive.Rig;
using MmdLive.Tracking;

namespace MmdLive;

internal static class ObservedSkeleton
{
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

    internal static void Draw(
        Graphics g,
        Motion m,
        RectangleF box,
        bool full,
        Pen rawPen,
        SolidBrush rawDot
    )
    {
        PointF Map(Vector3 p) =>
            new(box.X + p.X * box.Width / m.Width, box.Y + p.Y * box.Height / m.Height);
        void Dot(Vector3 p, float r)
        {
            if (!m.Sees(p))
                return;
            var a = Map(p);
            g.FillEllipse(rawDot, a.X - r, a.Y - r, r * 2, r * 2);
        }
        void Paths(Vector3[] points, int[][] paths, bool body = false)
        {
            foreach (var path in paths)
                for (int i = 1; i < path.Length; i++)
                {
                    int a = path[i - 1],
                        b = path[i];
                    if (
                        body
                            ? (!m.Sees(a) || !m.Sees(b) || (!full && (a > 24 || b > 24)))
                            : (!m.Sees(points[a]) || !m.Sees(points[b]))
                    )
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
                if (!m.Sees(p))
                    continue;
                var xy = new Vector2(p.X, p.Y);
                min = Vector2.Min(min, xy);
                max = Vector2.Max(max, xy);
            }
            if (!float.IsFinite(min.X))
                return;
            var a = Map(new(min, 0));
            var b = Map(new(max, 0));
            using var border = new Pen(Color.FromArgb(135, rawPen.Color), 1)
            {
                DashStyle = DashStyle.Dot,
            };
            g.DrawRectangle(border, a.X - 2, a.Y - 2, b.X - a.X + 4, b.Y - a.Y + 4);
        }
        if (m.Body.Length == 33)
        {
            Paths(m.Body, BodyPaths, true);
            for (int i = 11; i < (full ? 33 : 25); i++)
                if ((i < 17 || i > 22) && m.Sees(i))
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

    internal static void Save(
        OpenCvSharp.Mat source,
        string path,
        Motion motion,
        RigSnapshot pose,
        bool full
    )
    {
        using var output = PreviewMailbox.Downsample(source, Math.Max(source.Width, source.Height));
        using (var g = Graphics.FromImage(output))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float width = Math.Max(1.5f, output.Width / 700f);
            using var raw = new Pen(Color.FromArgb(59, 237, 215), width);
            using var brush = new SolidBrush(raw.Color);
            Draw(g, motion, new(0, 0, output.Width, output.Height), full, raw, brush);
            using var model = new Pen(Color.FromArgb(255, 176, 66), width);
            if (pose.Alignment is { } align)
                foreach (var bone in pose.Bones)
                {
                    var a = align.Pixel(bone.Start);
                    var b = align.Pixel(bone.End);
                    g.DrawLine(model, a.X, a.Y, b.X, b.Y);
                    g.DrawEllipse(model, a.X - width, a.Y - width, width * 2, width * 2);
                }
        }
        output.Save(path, ImageFormat.Png);
    }
}
