using System.Numerics;
using System.Text;
using Coocoo3D.Extensions.FileFormat;
using MmdLive.Tracking;

namespace MmdLive.Rig;

// Retargets one tracked Motion onto an MMD skeleton. The solve runs in stages, each in its own
// file: body aims (here), face (FaceSolve), hands (HandSolve), arms (UpperBody) and legs
// (LegSolve). Stages write targets into the per-frame buffers below; SolveHierarchy then
// eases each bone towards its target in parent-first order.
internal sealed partial class AvatarRig
{
    // Measured bones may turn at most this fast. Wrists may turn faster to follow palm roll.
    const float MaxTurnDegreesPerSecond = 540;
    const float MaxWristTurnDegreesPerSecond = 660;

    public PMXFormat Model { get; }
    public Matrix4x4[] Skin { get; }
    public float[] Weights { get; }
    public float[] EffectiveWeights => effective;
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public string Directory { get; }
    readonly Quaternion[] rotations,
        global;
    readonly Vector3[] positions;
    readonly int[] order;
    readonly Dictionary<string, int> bones = new(StringComparer.OrdinalIgnoreCase),
        morphs = new(StringComparer.OrdinalIgnoreCase);
    float[] neutral = [];
    readonly Grounding grounding = new();

    // Per-frame solve buffers, reused to keep the 60 Hz solve allocation free.
    readonly Quaternion[] targets;
    readonly Vector3[] translation;
    readonly float[] want,
        effective;
    readonly Dictionary<int, Quaternion> orientations = [];
    readonly Dictionary<int, (Vector3 rest, Vector3 target)> absolute = [];

    public void ResetPoseOrigin() => ResetLiveTracking();

    public AvatarRig(string path)
    {
        Directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var input = File.OpenRead(path);
        if (input.Length > 512L * 1024 * 1024)
            throw new InvalidDataException("角色文件超过 512 MiB");
        using var reader = new BinaryReader(input);
        Model = Path.GetExtension(path).Equals(".pmd", StringComparison.OrdinalIgnoreCase)
            ? new PmdReader(reader).Read()
            : PMXFormat.Load(reader);
        if (Model.Vertices.Length == 0 || Model.Bones.Count < 1)
            throw new InvalidDataException("角色网格或骨架为空");
        if (Model.TriangleIndexs.Any(i => i < 0 || i >= Model.Vertices.Length))
            throw new InvalidDataException("无效的网格索引");
        var points = Model.Vertices.Select(v => v.Coordinate);
        Min = points.Aggregate(Vector3.Min);
        Max = points.Aggregate(Vector3.Max);
        var count = Model.Bones.Count;
        rotations = Enumerable.Repeat(Quaternion.Identity, count).ToArray();
        global = new Quaternion[count];
        positions = new Vector3[count];
        Skin = new Matrix4x4[count];
        targets = new Quaternion[count];
        translation = new Vector3[count];
        Weights = new float[Math.Max(1, Model.Morphs.Count)];
        want = new float[Weights.Length];
        effective = new float[Weights.Length];
        IndexNames();
        order = Hierarchy(Model.Bones);
        ResolveMap();
        PrepareArms();
        Apply(Motion.Empty, true, 1);
    }

    static string Key(string s) =>
        s.Normalize(NormalizationForm.FormKC).Replace(" ", "").Replace("_", "").ToLowerInvariant();

    static void Add(Dictionary<string, int> map, string name, int index)
    {
        if (!string.IsNullOrWhiteSpace(name))
            map.TryAdd(Key(name), index);
    }

    // Name lookup for load time and diagnostics only. The per-frame solve reads BoneMap.
    int Bone(params string[] names) => Lookup(bones, names);

    static int Lookup(Dictionary<string, int> map, string[] names)
    {
        foreach (string n in names)
            if (map.TryGetValue(Key(n), out int i))
                return i;
        return -1;
    }

    static int[] Hierarchy(List<PMX_Bone> bones)
    {
        var result = new List<int>();
        var states = new byte[bones.Count];
        void Visit(int i)
        {
            if (states[i] == 2)
                return;
            if (states[i] == 1)
                throw new InvalidDataException("骨架包含循环父子关系");
            states[i] = 1;
            int p = bones[i].ParentIndex;
            if (p >= bones.Count)
                throw new InvalidDataException("无效的父骨骼");
            if (p >= 0)
                Visit(p);
            states[i] = 2;
            result.Add(i);
        }
        for (int i = 0; i < bones.Count; i++)
            Visit(i);
        return result.ToArray();
    }

    public float[] Neutral
    {
        get => neutral;
        set => neutral = value.Length == Blendshape.Count ? (float[])value.Clone() : [];
    }

    public void Bind(
        Dictionary<string, string> boneBindings,
        Dictionary<string, string> morphBindings
    )
    {
        IndexNames();
        foreach (var (canonical, name) in boneBindings)
        {
            int i = Find(Model.Bones, b => b.Name, b => b.NameEN, name);
            if (i >= 0)
                bones[Key(canonical)] = i;
        }
        foreach (var (canonical, name) in morphBindings)
        {
            int i = Find(Model.Morphs, m => m.Name, m => m.NameEN, name);
            if (i >= 0)
                morphs[Key(canonical)] = i;
        }
        ResolveMap();
        PrepareArms();
        ResetLiveTracking();
    }

    void IndexNames()
    {
        bones.Clear();
        morphs.Clear();
        for (int i = 0; i < Model.Bones.Count; i++)
        {
            Add(bones, Model.Bones[i].Name, i);
            Add(bones, Model.Bones[i].NameEN, i);
        }
        for (int i = 0; i < Model.Morphs.Count; i++)
        {
            Add(morphs, Model.Morphs[i].Name, i);
            Add(morphs, Model.Morphs[i].NameEN, i);
        }
    }

    // A saved binding names the exact model item. Fall back to the same normalized
    // comparison used for automatic matching so width, case or spacing changes still bind.
    static int Find<T>(List<T> items, Func<T, string> name, Func<T, string> nameEN, string wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted))
            return -1;
        int exact = items.FindIndex(x => name(x) == wanted);
        if (exact >= 0)
            return exact;
        string key = Key(wanted);
        return items.FindIndex(x =>
            (!string.IsNullOrWhiteSpace(name(x)) && Key(name(x)) == key)
            || (!string.IsNullOrWhiteSpace(nameEN(x)) && Key(nameEN(x)) == key)
        );
    }

    public void Calibrate(Motion m) => neutral = (float[])m.Expressions.Clone();

    public void Apply(
        Motion m,
        bool full,
        float smoothing,
        RigTrace? trace = null,
        float stepSeconds = 0
    )
    {
        Array.Fill(targets, Quaternion.Identity);
        Array.Clear(translation);
        Array.Clear(want);
        orientations.Clear();
        absolute.Clear();

        AimBody(m, full, trace);
        var headInCamera = SolveFace(m, trace);
        AimHands(m, trace);
        BlendMorphs(smoothing, m.Face.Length == FaceLandmark.Count, trace);
        GroundRoot(m, full, trace);
        SolveHierarchy(m, smoothing, stepSeconds, headInCamera, trace);
        PlantFeet(m, full, trace);
    }

    void Aim(int bone, int end, Vector3 direction, RigTrace? trace)
    {
        if (bone < 0 || end < 0 || direction.LengthSquared() < .001f)
            return;
        var rest = Model.Bones[end].Position - Model.Bones[bone].Position;
        if (rest.LengthSquared() > .0001f)
        {
            absolute[bone] = (rest, ToModel(direction));
            trace?.Mark(bone, end);
        }
    }

    void Rotate(int bone, Quaternion value, RigTrace? trace)
    {
        if (bone >= 0)
        {
            targets[bone] = value;
            trace?.Mark(bone);
        }
    }

    // Torso lean and the observed limb segments, aimed bone to bone.
    void AimBody(Motion m, bool full, RigTrace? trace)
    {
        if (m.Body.Length < PoseLandmark.Count)
            return;
        var p = m.Body;
        void Pair(int a, int b, int joint, int child)
        {
            if (m.Sees(a) && m.Sees(b))
                Aim(joint, child, p[b] - p[a], trace);
        }
        if (!full && map.Spine >= 0 && HalfSpine(m, out var facing))
        {
            orientations[map.Spine] = facing;
            trace?.Mark(map.Spine, map.Neck);
        }
        if (
            full
            && m.Sees(PoseLandmark.LeftShoulder)
            && m.Sees(PoseLandmark.RightShoulder)
            && m.Sees(PoseLandmark.LeftHip)
            && m.Sees(PoseLandmark.RightHip)
        )
        {
            // Image-relative Z is useful for locating limbs but exaggerates a torso seen from below.
            var torso = m.WorldBody.Length == PoseLandmark.Count ? m.WorldBody : p;
            var up =
                (
                    torso[PoseLandmark.LeftShoulder]
                    + torso[PoseLandmark.RightShoulder]
                    - torso[PoseLandmark.LeftHip]
                    - torso[PoseLandmark.RightHip]
                ) / 2;
            if (m.WorldBody.Length != PoseLandmark.Count)
                up.Z = 0;
            Aim(map.Spine, map.Neck, up, trace);
        }
        for (int side = 0; side < 2; side++)
        {
            var b = map.Sides[side];
            Pair(PoseLandmark.LeftShoulder + side, PoseLandmark.LeftElbow + side, b.Arm, b.Elbow);
            Pair(PoseLandmark.LeftElbow + side, PoseLandmark.LeftWrist + side, b.Elbow, b.Wrist);
            if (full)
            {
                Pair(PoseLandmark.LeftHip + side, PoseLandmark.LeftKnee + side, b.Leg, b.Knee);
                Pair(PoseLandmark.LeftKnee + side, PoseLandmark.LeftAnkle + side, b.Knee, b.Ankle);
                Pair(PoseLandmark.LeftHeel + side, PoseLandmark.LeftFootIndex + side, b.Ankle, b.Toe);
            }
        }
    }

    // Ease morph weights, expand group morphs and apply bone morphs to the targets.
    void BlendMorphs(float smoothing, bool faceSeen, RigTrace? trace)
    {
        for (int i = 0; i < want.Length; i++)
            Weights[i] += (want[i] - Weights[i]) * smoothing;
        Array.Copy(Weights, effective, Weights.Length);
        for (int i = 0; i < Model.Morphs.Count; i++)
            if (Model.Morphs[i].SubMorphs is { } items)
                foreach (var sub in items)
                    if ((uint)sub.GroupIndex < (uint)effective.Length)
                        effective[sub.GroupIndex] += Weights[i] * sub.Rate;
        for (int i = 0; i < Model.Morphs.Count; i++)
            if (effective[i] > .0001f && Model.Morphs[i].MorphBones is { } offsets)
                foreach (var x in offsets)
                    if ((uint)x.BoneIndex < (uint)targets.Length)
                    {
                        if (faceSeen)
                            trace?.Mark(x.BoneIndex);
                        translation[x.BoneIndex] += x.Translation * effective[i];
                        targets[x.BoneIndex] *= Quaternion.Slerp(
                            Quaternion.Identity,
                            x.Rotation,
                            effective[i]
                        );
                    }
    }

    void Compose(int i)
    {
        var bone = Model.Bones[i];
        int parent = bone.ParentIndex;
        var parentRotation = parent < 0 ? Quaternion.Identity : global[parent];
        global[i] = Quaternion.Normalize(parentRotation * rotations[i]);
        positions[i] =
            parent < 0
                ? bone.Position + translation[i]
                : positions[parent]
                    + Vector3.Transform(
                        bone.Position - Model.Bones[parent].Position + translation[i],
                        parentRotation
                    );
        Skin[i] =
            Matrix4x4.CreateTranslation(-bone.Position)
            * Matrix4x4.CreateFromQuaternion(global[i])
            * Matrix4x4.CreateTranslation(positions[i]);
    }

    // Shoulder parents first so PlaceArms sees the posed torso, then everything else.
    void SolveHierarchy(
        Motion m,
        float smoothing,
        float stepSeconds,
        Quaternion? headInCamera,
        RigTrace? trace
    )
    {
        foreach (int i in shoulderParents)
            Solve(i, smoothing, stepSeconds, headInCamera);
        PlaceArms(m, translation, absolute, trace, stepSeconds);
        foreach (int i in remainingBones)
            Solve(i, smoothing, stepSeconds, headInCamera);
    }

    void Solve(int i, float smoothing, float stepSeconds, Quaternion? headInCamera)
    {
        var b = Model.Bones[i];
        int parent = b.ParentIndex;
        var parentQ = parent < 0 ? Quaternion.Identity : global[parent];
        var target = targets[i];
        int head = map.Head;
        // The face mesh already measures the head in the camera frame, including torso motion.
        if (i == head && headInCamera is { } orientation)
            target = Quaternion.Inverse(parentQ) * orientation * target;
        if (orientations.TryGetValue(i, out var worldOrientation))
            target = Quaternion.Inverse(parentQ) * worldOrientation * target;
        if (absolute.TryGetValue(i, out var aim))
            target =
                stepSeconds > 0 && upperLimb[i]
                    // One measured ray specifies a swing, not axial twist. Transport the previous
                    // orientation so a forearm pointing back along its rest axis cannot spin.
                    ? Quaternion.Inverse(parentQ)
                        * FromTo(Vector3.Transform(aim.rest, global[i]), aim.target)
                        * global[i]
                    : FromTo(aim.rest, Vector3.Transform(aim.target, Quaternion.Inverse(parentQ)));
        if (
            (b.Flags & PMX_BoneFlag.AcquireRotate) != 0
            && (uint)b.AppendBoneIndex < (uint)rotations.Length
            && b.AppendBoneIndex != i
        )
            target = Quaternion.Normalize(
                target
                    * Quaternion.Slerp(
                        Quaternion.Identity,
                        rotations[b.AppendBoneIndex],
                        Math.Clamp(b.AppendBoneRatio, -1, 1)
                    )
            );
        bool measured = (upperLimb[i] && absolute.ContainsKey(i)) || orientations.ContainsKey(i);
        // Work in camera coordinates for a measured limb. Its parent's turn must not add a
        // second rotation to the wrist's own turn allowance during reacquisition.
        var from =
            stepSeconds > 0 && measured
                ? Quaternion.Normalize(Quaternion.Inverse(parentQ) * global[i])
                : rotations[i];
        float amount = measured ? Response(from, target, smoothing) : smoothing;
        // An unobserved limb, spine or head drifts back to rest slowly instead of snapping.
        if (
            (upperLimb[i] || i == map.Spine || (i == head && headInCamera == null))
            && !measured
            && Math.Abs(target.W) > .99999f
        )
            amount = 1 - MathF.Pow(1 - smoothing, .12f);
        if (stepSeconds > 0 && (measured || i == head))
        {
            float angle = 2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(from, target)), 0, 1));
            float maxTurn =
                (
                    orientations.ContainsKey(i) && upperLimb[i]
                        ? MaxWristTurnDegreesPerSecond
                        : MaxTurnDegreesPerSecond
                )
                * (MathF.PI / 180)
                * stepSeconds;
            if (angle > .0001f)
                amount = Math.Min(amount, maxTurn / angle);
        }
        rotations[i] = Quaternion.Normalize(Quaternion.Slerp(from, target, amount));
        Compose(i);
    }

    static Vector3 ToModel(Vector3 p) => new(-p.X, -p.Y, p.Z);

    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        if (a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f)
            return Quaternion.Identity;
        a = Vector3.Normalize(a);
        b = Vector3.Normalize(b);
        float dot = Vector3.Dot(a, b);
        if (dot > .99999f)
            return Quaternion.Identity;
        if (dot < -.99999f)
            return Quaternion.CreateFromAxisAngle(
                Vector3.Normalize(
                    Vector3.Cross(a, Math.Abs(a.Y) < .8f ? Vector3.UnitY : Vector3.UnitX)
                ),
                MathF.PI
            );
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(a, b), 1 + dot));
    }
}
