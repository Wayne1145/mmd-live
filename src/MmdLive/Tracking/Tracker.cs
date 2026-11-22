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
    // Hip-centered coordinates in meters, rotated back into camera axes.
    public Vector3[] WorldBody { get; init; } = [];

    // A pose landmark counts as seen at this visibility and presence score.
    internal const float VisibleScore = .55f;

    internal bool Sees(int index) =>
        index < Body.Length
        && index < Visibility.Length
        && Visibility[index] >= VisibleScore
        && Inside(Body[index], Width, Height);

    internal bool Sees(Vector3 point) => Inside(point, Width, Height);

    internal static bool Inside(Vector3 point, int width, int height) =>
        point.X >= 0
        && point.X < width
        && point.Y >= 0
        && point.Y < height
        && float.IsFinite(point.Z);

    public static Motion Empty { get; } = new(1, 1, [], [], [], [], [], 0);
}

internal sealed class Tracker : IDisposable
{
    // The body is tracked from last frame's crop. The full-image person detector only runs
    // every few frames to find a new performer.
    const int PersonRescanInterval = 8;

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

    // Per-model call counts and time, for the observe diagnostic.
    internal string Profile(int frames) =>
        string.Join(
            Environment.NewLine,
            models.Select(m =>
                $"{m.Name, -24} {m.Calls / (double)Math.Max(1, frames), 5:0.00} calls/frame  "
                + $"prepare {m.PrepareMilliseconds / Math.Max(1, frames), 6:0.00} ms/frame  "
                + $"infer {m.InferMilliseconds / Math.Max(1, frames), 6:0.00} ms/frame"
            )
        );

    public void Reset()
    {
        faceRegion = null;
        bodyRegion = null;
        frames = 0;
        lastHands = [];
        Array.Clear(lastPoseWrists);
    }

    public Motion Track(Mat image)
    {
        var clock = Stopwatch.StartNew();
        var whole = Crop.Whole(image);
        bool refresh = frames++ % PersonRescanInterval == 0;
        var (body, visible, world) = GetBody(image, whole, refresh);
        var (face, blend) = GetFace(image, whole, body, visible);
        if (body.Length == 0 && face.Length == FaceLandmark.Count)
        {
            var chin = XY(face[FaceLandmark.Chin]);
            var crown = XY(face[FaceLandmark.Forehead]);
            var vertical = chin - crown;
            float length = vertical.Length();
            if (length > 12)
            {
                bodyRegion = new(chin + vertical * 2.05f, length * 6.9f, Crop.Upright(chin, crown));
                (body, visible, world) = GetBody(image, whole, false);
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
        )
        {
            WorldBody = world,
        };
    }

    (Vector3[], float[], Vector3[]) GetBody(Mat image, Crop whole, bool refresh)
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
            return ([], [], []);
        var raw = pose.Image(image, c, false, "Identity", "Identity_1", "Identity_4");
        if (raw[1][0] < .5f)
        {
            bodyRegion = null;
            return ([], [], []);
        }
        var points = Project(raw[0], c, pose.Size, 5, PoseLandmark.Count);
        var vis = new float[PoseLandmark.Count];
        for (int i = 0; i < PoseLandmark.Count; i++)
            vis[i] = Math.Min(
                BlazeDetector.Sigmoid(raw[0][i * 5 + 3]),
                BlazeDetector.Sigmoid(raw[0][i * 5 + 4])
            );
        var world = new Vector3[PoseLandmark.Count];
        for (int i = 0; i < world.Length; i++)
        {
            int k = i * 3;
            var xy = c.Rotate(new(raw[2][k], raw[2][k + 1]));
            world[i] = new(xy, raw[2][k + 2]);
        }
        return (points, vis, world);
    }

    IEnumerable<Crop> FaceCandidates(Mat image, Crop whole, Vector3[] body, float[] visible)
    {
        if (faceRegion is Crop tracked)
            yield return tracked;

        Crop? Detect(Crop search, float threshold)
        {
            var d = BlazeDetector
                .Find(faceDetector, image, search, true, threshold, 1)
                .FirstOrDefault();
            if (d is null)
                return null;
            var eyes = d.Marks[1] - d.Marks[0];
            return new(d.Center, Math.Max(d.Size.X, d.Size.Y) * 1.5f, MathF.Atan2(eyes.Y, eyes.X));
        }

        // At full-body distances the global 128px detector can mistake clothing for a face.
        // Validate each proposal with the mesh before it can displace the tracked performer.
        if (body.Length == PoseLandmark.Count && visible[PoseLandmark.Nose] > .4f)
        {
            float headWidth = Math.Max(
                Vector2.Distance(XY(body[PoseLandmark.LeftEar]), XY(body[PoseLandmark.RightEar])),
                2.1f * Vector2.Distance(XY(body[PoseLandmark.LeftEye]), XY(body[PoseLandmark.RightEye]))
            );
            if (headWidth > 4)
            {
                var nose = XY(body[PoseLandmark.Nose]);
                if (Detect(new(nose, headWidth * 3.4f), .48f) is Crop head)
                    yield return head;
                yield return new(nose, headWidth * 2.1f);
            }
        }
        if (Detect(whole, .55f) is Crop global)
            yield return global;
    }

    (Vector3[], float[]) GetFace(Mat image, Crop whole, Vector3[] body, float[] visible)
    {
        foreach (var c in FaceCandidates(image, whole, body, visible))
        {
            if (c.Side < 8)
                continue;
            var raw = mesh.Image(image, c, false, "Identity", "Identity_1");
            if (BlazeDetector.Sigmoid(raw[1][0]) < .55f)
                continue;
            var points = Project(raw[0], c, mesh.Size, 3, FaceLandmark.Count);
            if (
                !Motion.Inside(points[FaceLandmark.NoseTip], image.Width, image.Height)
                || points.Count(p => Motion.Inside(p, image.Width, image.Height)) < FaceLandmark.Count / 2
            )
                continue;
            var input = new float[subset.Length * 2];
            for (int i = 0; i < subset.Length; i++)
            {
                input[i * 2] = points[subset[i]].X;
                input[i * 2 + 1] = points[subset[i]].Y;
            }
            var eyes = points[FaceLandmark.LeftEyeOuter] - points[FaceLandmark.RightEyeOuter];
            faceRegion = Bounds(points.Select(XY), MathF.Atan2(eyes.Y, eyes.X), 1.52f, 0);
            return (points, expressions.Run(input)[0]);
        }
        faceRegion = null;
        return ([], []);
    }

    Hand[] lastHands = [];
    readonly Vector2?[] lastPoseWrists = new Vector2?[2];

    Hand[] GetHands(Mat image, Crop whole, Vector3[] body, float[] visible)
    {
        var candidates = new List<HandCandidate>(2);
        var tried = new List<Detection>(4);
        HandCandidate? Landmarks(Crop region, float minPresence, bool? trackedSide = null)
        {
            if (region.Side < 8)
                return null;
            var raw = hand.Image(image, region, false, "Identity", "Identity_1", "Identity_2");
            if (raw[1][0] < minPresence)
                return null;
            var points = Project(raw[0], region, hand.Size, 3, HandLandmark.Count);
            bool InFrame(int i) => Motion.Inside(points[i], image.Width, image.Height);
            if (
                !InFrame(HandLandmark.Wrist)
                || !(
                    InFrame(HandLandmark.IndexBase)
                    || InFrame(HandLandmark.MiddleBase)
                    || InFrame(HandLandmark.RingBase)
                    || InFrame(HandLandmark.LittleBase)
                )
            )
                return null;
            float separation = Math.Max(6, Vector2.Distance(XY(points[0]), XY(points[9])) * .30f);
            if (
                candidates.Any(c =>
                    Vector2.Distance(XY(c.Points[0]), XY(points[0])) < separation
                    && Vector2.Distance(XY(c.Points[9]), XY(points[9])) < separation
                )
            )
                return null;
            return new(raw[2][0], raw[1][0], points, trackedSide);
        }
        Vector2? PoseWrist(int side) =>
            body.Length == PoseLandmark.Count
            && visible[PoseLandmark.LeftWrist + side] >= Motion.VisibleScore
            && Motion.Inside(body[PoseLandmark.LeftWrist + side], image.Width, image.Height)
                ? XY(body[PoseLandmark.LeftWrist + side])
                : null;

        void Measure(Detection d)
        {
            if (
                tried.Any(other =>
                    Vector2.Distance(other.Center, d.Center) < Math.Max(6, d.Size.X * .28f)
                )
                || candidates.Any(c =>
                    Vector2.Distance(XY(c.Points[0]), d.Marks[0]) < d.Size.X * .48f
                )
            )
                return;
            tried.Add(d);
            var region = Bounds(d.Marks, Crop.Upright(d.Marks[0], d.Marks[2]), 3, -.4f);
            if (Landmarks(region, .6f) is { } measured)
                candidates.Add(measured);
        }
        foreach (var d in BlazeDetector.Find(palm, image, whole, false, .5f, 2))
            Measure(d);
        void Search(Vector2 center, float size)
        {
            if (
                size < 16
                || candidates.Any(h => Vector2.Distance(XY(h.Points[0]), center) < size * .23f)
            )
                return;
            var detection = BlazeDetector
                .Find(palm, image, new Crop(center, size), false, .53f, 1)
                .FirstOrDefault();
            if (detection != null)
                Measure(detection);
        }
        if (body.Length == PoseLandmark.Count && candidates.Count < 2)
        {
            float shoulderSpan = Vector2.Distance(XY(body[PoseLandmark.LeftShoulder]), XY(body[PoseLandmark.RightShoulder]));
            for (int side = 0; side < 2; side++)
            {
                int wrist = PoseLandmark.LeftWrist + side,
                    elbow = PoseLandmark.LeftElbow + side;
                if (visible[wrist] < Motion.VisibleScore || !Motion.Inside(body[wrist], image.Width, image.Height))
                    continue;
                var forearm = XY(body[wrist]) - XY(body[elbow]);
                float span = Math.Max(shoulderSpan * .40f, forearm.Length());
                Search(XY(body[wrist]) + forearm * .16f, Math.Max(42, span * 2.2f));
            }
        }
        // The last palm locates a crop. All 21 points and presence come from the current image.
        if (candidates.Count < 2)
            foreach (var previous in lastHands)
            {
                int side = previous.Left ? 0 : 1;
                var p = previous.Points;
                var wrist = XY(p[0]);
                if (
                    candidates.Any(c =>
                        Vector2.Distance(XY(c.Points[0]), wrist)
                        < Vector2.Distance(wrist, XY(p[9])) * .6f
                    )
                )
                    continue;
                var axis = (XY(p[5]) + XY(p[9]) + XY(p[13])) / 3;
                var region = Bounds(p.Select(XY), Crop.Upright(wrist, axis), 1.65f, -.04f);
                var offset = Vector2.Zero;
                if (
                    PoseWrist(side) is { } current
                    && lastPoseWrists[side] is { } last
                    && Vector2.Distance(wrist, last) < region.Side * .45f
                    && Vector2.Distance(current, last) < region.Side * .85f
                )
                    offset = current - last;
                region = region with { Center = region.Center + offset };
                if (
                    Landmarks(region, .80f, previous.Left) is not { } measured
                    || Vector2.Distance(XY(measured.Points[0]), wrist + offset) > region.Side * .36f
                )
                    continue;
                candidates.Add(measured);
            }
        // If direct landmark tracking failed, try a local palm detector before giving up.
        if (candidates.Count < 2)
            foreach (var previous in lastHands)
            {
                var wrist = XY(previous.Points[0]);
                var center = (wrist + XY(previous.Points[9])) / 2;
                float radius = previous.Points.Max(p => Vector2.Distance(center, XY(p)));
                Search(center, Math.Max(42, radius * 3.5f));
            }
        // A newly visible hand can lack a palm box. Pose index and pinky supply only the crop.
        if (candidates.Count < 2)
            for (int side = 0; side < 2; side++)
            {
                if (
                    PoseWrist(side) is not { } wrist
                    || visible[PoseLandmark.LeftPinky + side] < Motion.VisibleScore
                    || visible[PoseLandmark.LeftIndex + side] < Motion.VisibleScore
                )
                    continue;
                var tips = (XY(body[PoseLandmark.LeftPinky + side]) + XY(body[PoseLandmark.LeftIndex + side])) / 2;
                var axis = tips - wrist;
                float reach = axis.Length();
                if (
                    reach < 9
                    || candidates.Any(c => Vector2.Distance(XY(c.Points[0]), wrist) < reach * .8f)
                )
                    continue;
                var region = new Crop(wrist + axis * .58f, reach * 3.2f, Crop.Upright(wrist, tips));
                if (
                    Landmarks(region, .80f) is { } measured
                    && Vector2.Distance(XY(measured.Points[0]), wrist) < reach * .8f
                )
                    candidates.Add(measured);
            }
        for (int side = 0; side < 2; side++)
            lastPoseWrists[side] = PoseWrist(side);
        lastHands = AssignHands(candidates, body, visible);
        return lastHands;
    }

    internal sealed record HandCandidate(
        float RightProbability,
        float Presence,
        Vector3[] Points,
        bool? TrackedSide = null
    );

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
            bool left =
                candidate.TrackedSide is { } tracked
                && candidate.RightProbability is > .25f and < .75f
                    ? tracked
                    : candidate.RightProbability < .5f;
            float confidence = left ? 1 - candidate.RightProbability : candidate.RightProbability;
            var wrist = XY(candidate.Points[0]);
            float reach = Math.Max(16, candidate.Points.Max(p => Vector2.Distance(wrist, XY(p))));
            float LeftOrRightDistance(int index) =>
                index < body.Length && index < visibility.Length && visibility[index] > .45f
                    ? Vector2.Distance(wrist, XY(body[index]))
                    : float.PositiveInfinity;
            float leftDistance = LeftOrRightDistance(PoseLandmark.LeftWrist);
            float rightDistance = LeftOrRightDistance(PoseLandmark.LeftWrist + 1);
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
