using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using OpenCvSharp;

namespace MmdLive.Tracking;

internal sealed record Hand(bool Left, float Confidence, Vector3[] Points);

internal sealed record Motion(
    int Width,
    int Height,
    Vector3[] Face,
    float[] Expressions,
    Vector3[] Body,
    float[] Visibility,
    Hand[] Hands,
    double Milliseconds
)
{
    public static Motion Empty { get; } = new(1, 1, [], [], [], [], [], 0);
}

internal sealed class Tracker : IDisposable
{
    readonly List<GpuModel> models = [];
    readonly GpuModel faceDetector,
        mesh,
        expressions,
        person,
        pose,
        palm,
        hand;
    readonly int[] subset;
    Crop? faceRegion,
        bodyRegion;
    int frames;

    public Tracker(string directory, int adapter)
    {
        GpuModel Load(string name)
        {
            var m = new GpuModel(Path.Combine(directory, name + ".onnx"), adapter);
            models.Add(m);
            return m;
        }
        try
        {
            subset = JsonSerializer.Deserialize<int[]>(
                File.ReadAllText(Path.Combine(directory, "blendshape_subset.json"))
            )!;
            faceDetector = Load("face_detector");
            mesh = Load("face_landmarks_detector");
            expressions = Load("face_blendshapes");
            person = Load("person");
            pose = Load("pose");
            palm = Load("palm");
            hand = Load("hand");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Reset()
    {
        faceRegion = null;
        bodyRegion = null;
        frames = 0;
    }

    public Motion Track(Mat image)
    {
        var clock = Stopwatch.StartNew();
        var whole = Crop.Whole(image);
        bool refresh = frames++ % 8 == 0;
        var (body, visible) = GetBody(image, whole, refresh);
        var (face, blend) = GetFace(image, whole, body, refresh);
        if (body.Length == 0 && face.Length == 478)
        {
            var chin = XY(face[152]);
            var crown = XY(face[10]);
            var vertical = chin - crown;
            float length = vertical.Length();
            if (length > 12)
            {
                bodyRegion = new(chin + vertical * 2.05f, length * 6.9f, Crop.Upright(chin, crown));
                (body, visible) = GetBody(image, whole, false);
            }
        }
        var hands = GetHands(image, whole, body, visible);
        return new(
            image.Width,
            image.Height,
            face,
            blend,
            body,
            visible,
            hands,
            clock.Elapsed.TotalMilliseconds
        );
    }

    (Vector3[], float[]) GetBody(Mat image, Crop whole, bool refresh)
    {
        if (refresh || bodyRegion is null)
        {
            var d = BlazeDetector.Find(person, image, whole, true, .52f, 1).FirstOrDefault();
            if (d != null)
                bodyRegion = new(
                    d.Marks[0],
                    2 * Vector2.Distance(d.Marks[0], d.Marks[1]),
                    Crop.Upright(d.Marks[0], d.Marks[1])
                );
        }
        if (bodyRegion is not Crop c || c.Side < 8)
            return ([], []);
        var raw = pose.Image(image, c, false, "Identity", "Identity_1");
        if (raw[1][0] < .5f)
        {
            bodyRegion = null;
            return ([], []);
        }
        var points = Project(raw[0], c, pose.Size, 5, 33);
        var vis = new float[33];
        for (int i = 0; i < 33; i++)
            vis[i] = Math.Min(
                BlazeDetector.Sigmoid(raw[0][i * 5 + 3]),
                BlazeDetector.Sigmoid(raw[0][i * 5 + 4])
            );
        return (points, vis);
    }

    (Vector3[], float[]) GetFace(Mat image, Crop whole, Vector3[] body, bool refresh)
    {
        if (refresh || faceRegion is null)
        {
            var d = BlazeDetector.Find(faceDetector, image, whole, true, .55f, 1).FirstOrDefault();
            if (d is null && body.Length > 0)
            {
                float width = Vector2.Distance(XY(body[7]), XY(body[8]));
                float headWidth = Math.Max(
                    width,
                    2.1f * Vector2.Distance(XY(body[2]), XY(body[5]))
                );
                if (headWidth > 4)
                {
                    d = BlazeDetector
                        .Find(
                            faceDetector,
                            image,
                            new Crop(XY(body[0]), headWidth * 3.4f),
                            true,
                            .48f,
                            1
                        )
                        .FirstOrDefault();
                    if (d == null)
                        faceRegion = new(XY(body[0]), headWidth * 2.1f);
                }
            }
            if (d != null)
                faceRegion = new(
                    d.Center,
                    Math.Max(d.Size.X, d.Size.Y) * 1.5f,
                    MathF.Atan2(d.Marks[1].Y - d.Marks[0].Y, d.Marks[1].X - d.Marks[0].X)
                );
        }
        if (faceRegion is not Crop c || c.Side < 8)
            return ([], []);
        var raw = mesh.Image(image, c, false, "Identity", "Identity_1");
        if (BlazeDetector.Sigmoid(raw[1][0]) < .55f)
        {
            faceRegion = null;
            return ([], []);
        }
        var points = Project(raw[0], c, mesh.Size, 3, 478);
        var input = new float[subset.Length * 2];
        for (int i = 0; i < subset.Length; i++)
        {
            input[i * 2] = points[subset[i]].X;
            input[i * 2 + 1] = points[subset[i]].Y;
        }
        var angle = MathF.Atan2(points[263].Y - points[33].Y, points[263].X - points[33].X);
        faceRegion = Bounds(points.Select(XY), angle, 1.52f, 0);
        return (points, expressions.Run(input)[0]);
    }

    Hand[] GetHands(Mat image, Crop whole, Vector3[] body, float[] visible)
    {
        var palms = BlazeDetector.Find(palm, image, whole, false, .5f, 2);
        // A 1440p full-body frame can contain hands too small for the 192-pixel global detector.
        if (body.Length > 0 && palms.Count < 2)
            foreach (var (wrist, elbow) in new[] { (15, 13), (16, 14) })
            {
                if (visible[wrist] < .45f)
                    continue;
                var w = XY(body[wrist]);
                float length = Vector2.Distance(w, XY(body[elbow]));
                if (
                    palms.Any(x =>
                        Vector2.Distance(w, x.Center) < Math.Max(x.Size.X, length * .52f)
                    )
                )
                    continue;
                var area = new Crop(w + (w - XY(body[elbow])) * .18f, Math.Max(42, length * 2.2f));
                var found = BlazeDetector.Find(palm, image, area, false, .53f, 1).FirstOrDefault();
                if (
                    found != null
                    && palms.All(x =>
                        Vector2.Distance(x.Center, found.Center) > Math.Max(10, found.Size.X * .65f)
                    )
                )
                    palms.Add(found);
            }
        var candidates = new List<HandCandidate>(palms.Count);
        foreach (var d in palms.Take(2))
        {
            var region = Bounds(d.Marks, Crop.Upright(d.Marks[0], d.Marks[2]), 3, -.4f);
            if (region.Side < 8)
                continue;
            var raw = hand.Image(image, region, false, "Identity", "Identity_1", "Identity_2");
            if (raw[1][0] < .55f)
                continue;
            var pts = Project(raw[0], region, hand.Size, 3, 21);
            candidates.Add(new HandCandidate(raw[2][0], raw[1][0], pts));
        }
        return AssignHands(candidates, body, visible);
    }

    internal sealed record HandCandidate(float RightProbability, float Presence, Vector3[] Points);

    internal static Hand[] AssignHands(
        IReadOnlyList<HandCandidate> candidates,
        Vector3[] body,
        float[] visibility
    )
    {
        var bySide = new Dictionary<bool, (Hand hand, float strength)>();
        foreach (var candidate in candidates)
        {
            // OpenCV Zoo handpose Identity_2 is P(right). A crop preserves the source orientation.
            bool left = candidate.RightProbability < .5f;
            float confidence = Math.Max(candidate.RightProbability, 1 - candidate.RightProbability);
            var wrist = XY(candidate.Points[0]);
            float reach = Math.Max(16, candidate.Points.Max(p => Vector2.Distance(wrist, XY(p))));
            float LeftOrRightDistance(int index) =>
                index < body.Length && index < visibility.Length && visibility[index] > .45f
                    ? Vector2.Distance(wrist, XY(body[index]))
                    : float.PositiveInfinity;
            float leftDistance = LeftOrRightDistance(15);
            float rightDistance = LeftOrRightDistance(16);
            float nearest = Math.Min(leftDistance, rightDistance);
            if (
                nearest < Math.Max(12, reach * .35f)
                && Math.Abs(leftDistance - rightDistance) > reach * .2f
            )
            {
                left = leftDistance < rightDistance;
                confidence = Math.Max(confidence, 1 - nearest / reach);
            }
            // A single avatar has one wrist per side. Duplicate crops compete for the same wrist.
            float strength = candidate.Presence * confidence;
            if (!bySide.TryGetValue(left, out var previous) || strength > previous.strength)
                bySide[left] = (new Hand(left, candidate.Presence, candidate.Points), strength);
        }
        return bySide.Values.Select(x => x.hand).ToArray();
    }

    static Vector3[] Project(float[] data, Crop c, int size, int stride, int count)
    {
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int k = i * stride;
            var pixel = c.Pixel(new Vector2(data[k], data[k + 1]) / size);
            result[i] = new(pixel, data[k + 2] * c.Side / size);
        }
        return result;
    }

    static Crop Bounds(IEnumerable<Vector2> points, float angle, float scale, float shiftY)
    {
        var inverse = new Crop(Vector2.Zero, 1, -angle);
        var rotated = points.Select(inverse.Rotate).ToArray();
        var min = rotated.Aggregate(Vector2.Min);
        var max = rotated.Aggregate(Vector2.Max);
        var center = (min + max) / 2;
        center.Y += (max.Y - min.Y) * shiftY;
        return new(
            new Crop(Vector2.Zero, 1, angle).Rotate(center),
            Math.Max(max.X - min.X, max.Y - min.Y) * scale,
            angle
        );
    }

    static Vector2 XY(Vector3 p) => new(p.X, p.Y);

    public void Dispose()
    {
        foreach (var m in models)
            m.Dispose();
        models.Clear();
    }
}
