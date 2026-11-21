using System.Numerics;
using System.Text;
using Coocoo3D.Extensions.FileFormat;
using MmdLive.Tracking;

namespace MmdLive.Rig;

internal sealed partial class AvatarRig
{
    public PMXFormat Model { get; }
    public Matrix4x4[] Skin { get; }
    public float[] Weights { get; }
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
    static readonly string[] LeftBlink = ["ウィンク", "ウィンク左", "まばたき左", "blinkleft"];
    static readonly string[] RightBlink = ["ウィンク右", "まばたき右", "winkright", "blinkright"];

    public void ResetPoseOrigin() => grounding.Reset();

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
        Weights = new float[Math.Max(1, Model.Morphs.Count)];
        for (int i = 0; i < count; i++)
        {
            Add(bones, Model.Bones[i].Name, i);
            Add(bones, Model.Bones[i].NameEN, i);
        }
        for (int i = 0; i < Model.Morphs.Count; i++)
        {
            Add(morphs, Model.Morphs[i].Name, i);
            Add(morphs, Model.Morphs[i].NameEN, i);
        }
        order = Hierarchy(Model.Bones);
        Apply(Motion.Empty, true, 1);
    }

    static string Key(string s) =>
        s.Normalize(NormalizationForm.FormKC).Replace(" ", "").Replace("_", "").ToLowerInvariant();

    static void Add(Dictionary<string, int> map, string name, int index)
    {
        if (!string.IsNullOrWhiteSpace(name))
            map.TryAdd(Key(name), index);
    }

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
        set => neutral = value.Length == 52 ? (float[])value.Clone() : [];
    }

    public void Bind(
        Dictionary<string, string> boneBindings,
        Dictionary<string, string> morphBindings
    )
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
        foreach (var (canonical, name) in boneBindings)
        {
            int i = Model.Bones.FindIndex(b => b.Name == name);
            if (i >= 0)
                bones[Key(canonical)] = i;
        }
        foreach (var (canonical, name) in morphBindings)
        {
            int i = Model.Morphs.FindIndex(m => m.Name == name);
            if (i >= 0)
                morphs[Key(canonical)] = i;
        }
    }

    public void Calibrate(Motion m) => neutral = (float[])m.Expressions.Clone();

    public void Apply(Motion m, bool full, float smoothing, RigTrace? trace = null)
    {
        var targets = Enumerable.Repeat(Quaternion.Identity, rotations.Length).ToArray();
        var absolute = new Dictionary<int, (Vector3 rest, Vector3 target)>();
        void Aim(int bone, int end, Vector3 direction)
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
        void Rotate(int bone, Quaternion value)
        {
            if (bone >= 0)
            {
                targets[bone] = value;
                trace?.Mark(bone);
            }
        }
        if (m.Body.Length >= 33)
        {
            var p = m.Body;
            void Pair(int a, int b, int joint, int child)
            {
                if (m.Visibility[a] > .38f && m.Visibility[b] > .38f)
                    Aim(joint, child, p[b] - p[a]);
            }
            if (
                m.Visibility[11] > .4f
                && m.Visibility[12] > .4f
                && Math.Min(m.Visibility[23], m.Visibility[24]) > .3f
            )
            {
                // Image-relative Z is useful for locating limbs but exaggerates a torso seen from below.
                var torso = m.WorldBody.Length == 33 ? m.WorldBody : p;
                var up = (torso[11] + torso[12] - torso[23] - torso[24]) / 2;
                if (m.WorldBody.Length != 33)
                    up.Z = 0;
                Aim(Bone("上半身", "spine"), Bone("首", "neck", "上半身2"), up);
            }
            foreach (bool left in new[] { true, false })
            {
                string s = left ? "左" : "右",
                    e = left ? "left" : "right";
                int k = left ? 0 : 1;
                int arm = Bone(s + "腕", e + "arm", e + "upperarm"),
                    elbow = Bone(s + "ひじ", s + "肘", e + "elbow", e + "lowerarm"),
                    wrist = Bone(s + "手首", e + "wrist", e + "hand");
                Pair(11 + k, 13 + k, arm, elbow);
                Pair(13 + k, 15 + k, elbow, wrist);
                if (full)
                {
                    int leg = Bone(s + "足", e + "leg", e + "upperleg"),
                        knee = Bone(s + "ひざ", s + "膝", e + "knee", e + "lowerleg"),
                        ankle = Bone(s + "足首", e + "ankle", e + "foot");
                    Pair(23 + k, 25 + k, leg, knee);
                    Pair(25 + k, 27 + k, knee, ankle);
                    Pair(29 + k, 31 + k, ankle, Bone(s + "つま先", e + "toe"));
                }
            }
        }
        var want = new float[Weights.Length];
        float E(int i) =>
            m.Expressions.Length == 52
                ? Math.Clamp(
                    (m.Expressions[i] - (neutral.Length == 52 ? neutral[i] : 0)) * 1.18f,
                    0,
                    1
                )
                : 0;
        void Morph(float amount, params string[] names)
        {
            int i = Lookup(morphs, names);
            if (i >= 0)
                want[i] = Math.Max(want[i], amount);
        }
        int head = Bone("頭", "head");
        Quaternion? headInCamera = null;
        if (m.Face.Length == 478)
        {
            var f = m.Face;
            var eyes = f[263] - f[33];
            var vertical = f[152] - f[10];
            var yaw = MathF.Atan2(eyes.Z, Math.Max(1, Math.Abs(eyes.X)));
            var pitch = -MathF.Atan2(vertical.Z, Math.Max(1, Math.Abs(vertical.Y)));
            var roll = MathF.Atan2(eyes.Y, eyes.X);
            headInCamera = Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
            trace?.Mark(head);
            float blinkL = E(9),
                blinkR = E(10);
            if (Lookup(morphs, LeftBlink) >= 0 && Lookup(morphs, RightBlink) >= 0)
            {
                Morph(blinkL, LeftBlink);
                Morph(blinkR, RightBlink);
            }
            else
                Morph(Math.Max(blinkL, blinkR), "まばたき", "瞬き", "blink", "eyesclosed");
            Morph(E(25), "あ", "a", "jawopen");
            Morph(E(44) * .55f + E(45) * .45f, "い", "i");
            Morph(E(38), "う", "u", "pucker");
            Morph(E(32), "お", "o", "funnel");
            Morph((E(44) + E(45)) * .3f, "え", "e");
            Morph((E(44) + E(45)) * .5f, "にやり", "口角上げ", "smile");
            Morph(E(3), "上", "browup");
            Morph((E(1) + E(2)) * .5f, "怒り", "browdown");
            void Eye(int i, int inner, int outer, int top, int bottom, string jp, string en)
            {
                var center = (f[inner] + f[outer]) / 2;
                float w = Math.Max(2, Math.Abs(f[inner].X - f[outer].X));
                float h = Math.Max(w * .23f, Math.Abs(f[top].Y - f[bottom].Y));
                float x = Math.Clamp((f[i].X - center.X) / w, -.25f, .25f),
                    y = Math.Clamp((f[i].Y - (f[top].Y + f[bottom].Y) / 2) / h, -.4f, .4f);
                Rotate(Bone(jp, en), Quaternion.CreateFromYawPitchRoll(x * 1.8f, -y * .6f, 0));
            }
            Eye(473, 362, 263, 386, 374, "左目", "lefteye");
            Eye(468, 133, 33, 159, 145, "右目", "righteye");
        }
        foreach (var h in m.Hands)
        {
            string s = h.Left ? "左" : "右",
                e = h.Left ? "left" : "right";
            var p = h.Points;
            string[] jp = ["親指", "人指", "中指", "薬指", "小指"];
            string[] en = ["thumb", "index", "middle", "ring", "little"];
            int Digit(int digit, int segment)
            {
                int n = segment + (digit == 0 ? 0 : 1);
                string suffix = n.ToString();
                return Bone(
                    s + jp[digit] + suffix,
                    s + (digit == 1 ? "人差指" : jp[digit]) + suffix,
                    e + en[digit] + (segment + 1),
                    s + jp[digit] + (segment == 0 ? "根" : "先")
                );
            }
            Aim(Bone(s + "手首", e + "wrist", e + "hand"), Digit(2, 0), p[9] - p[0]);
            for (int finger = 0; finger < 5; finger++)
            for (int part = 0; part < 3; part++)
            {
                int joint = Digit(finger, part),
                    next = part < 2 ? Digit(finger, part + 1) : -1;
                if (joint < 0)
                    continue;
                int k = 1 + 4 * finger + part;
                var direction = p[k + 1] - p[k];
                if (next >= 0)
                    Aim(joint, next, direction);
                else
                {
                    int prev = Digit(finger, Math.Max(0, part - 1));
                    if (prev >= 0 && prev != joint)
                    {
                        var tail = Model.Bones[joint].Position - Model.Bones[prev].Position;
                        absolute[joint] = (tail, ToModel(direction));
                        trace?.Mark(joint, offset: tail);
                    }
                }
            }
        }
        for (int i = 0; i < want.Length; i++)
            Weights[i] += (want[i] - Weights[i]) * smoothing;
        var effective = (float[])Weights.Clone();
        for (int i = 0; i < Model.Morphs.Count; i++)
            if (Model.Morphs[i].SubMorphs is { } items)
                foreach (var sub in items)
                    if ((uint)sub.GroupIndex < (uint)effective.Length)
                        effective[sub.GroupIndex] += Weights[i] * sub.Rate;
        var translation = new Vector3[targets.Length];
        for (int i = 0; i < Model.Morphs.Count; i++)
            if (effective[i] > .0001f && Model.Morphs[i].MorphBones is { } offsets)
                foreach (var x in offsets)
                    if ((uint)x.BoneIndex < (uint)targets.Length)
                    {
                        trace?.Mark(x.BoneIndex);
                        translation[x.BoneIndex] += x.Translation * effective[i];
                        targets[x.BoneIndex] *= Quaternion.Slerp(
                            Quaternion.Identity,
                            x.Rotation,
                            effective[i]
                        );
                    }
        if (full && m.Body.Length == 33)
        {
            int l = Bone("左腕", "leftarm", "leftupperarm"),
                r = Bone("右腕", "rightarm", "rightupperarm"),
                center = Bone("センター", "center", "下半身", "hips");
            float height = Max.Y - Min.Y;
            float shoulders =
                l >= 0 && r >= 0
                    ? Vector3.Distance(Model.Bones[l].Position, Model.Bones[r].Position)
                    : height * .22f;
            var delta = grounding.Observe(m, shoulders, height);
            if (center >= 0)
            {
                translation[center] += delta;
                if (m.Visibility[23] >= .45f && m.Visibility[24] >= .45f)
                    trace?.Mark(center);
            }
        }
        else
            grounding.Reset();
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
        foreach (int i in order)
        {
            var b = Model.Bones[i];
            int parent = b.ParentIndex;
            var parentQ = parent < 0 ? Quaternion.Identity : global[parent];
            var target = targets[i];
            // The face mesh already measures the head in the camera frame, including torso motion.
            if (i == head && headInCamera is { } orientation)
                target = Quaternion.Inverse(parentQ) * orientation * target;
            if (absolute.TryGetValue(i, out var aim))
                target = FromTo(
                    aim.rest,
                    Vector3.Transform(aim.target, Quaternion.Inverse(parentQ))
                );
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
            rotations[i] = Quaternion.Slerp(rotations[i], target, smoothing);
            Compose(i);
        }
        if (full && m.Body.Length == 33)
        {
            foreach (bool left in new[] { true, false })
            {
                string side = left ? "左" : "右",
                    en = left ? "left" : "right";
                int hip = Bone(side + "足", en + "leg", en + "upperleg"),
                    knee = Bone(side + "ひざ", side + "膝", en + "knee", en + "lowerleg"),
                    ankle = Bone(side + "足首", en + "ankle", en + "foot");
                if (
                    hip < 0
                    || knee < 0
                    || ankle < 0
                    || !grounding.Plant(left, positions[ankle], out var target)
                )
                    continue;
                if (Vector3.DistanceSquared(positions[ankle], target) < .00001f)
                    continue;
                var axis = target - positions[hip];
                float length = axis.Length();
                if (length < .001f)
                    continue;
                axis /= length;
                float upper = Vector3.Distance(positions[knee], positions[hip]),
                    lower = Vector3.Distance(positions[ankle], positions[knee]);
                if (upper < .001f || lower < .001f)
                    continue;
                float span = Math.Clamp(
                    length,
                    Math.Abs(upper - lower) + .0001f,
                    upper + lower - .0001f
                );
                float along = (upper * upper - lower * lower + span * span) / (2 * span);
                var bend = positions[knee] - positions[hip];
                bend -= Vector3.Dot(bend, axis) * axis;
                if (bend.LengthSquared() < .00001f)
                    bend = -Vector3.UnitZ + axis * axis.Z;
                if (bend.LengthSquared() < .00001f)
                    continue;
                var kneeTarget =
                    positions[hip]
                    + axis * along
                    + Vector3.Normalize(bend)
                        * MathF.Sqrt(Math.Max(0, upper * upper - along * along));
                void Correct(int joint, int end, Vector3 direction)
                {
                    int parent = Model.Bones[joint].ParentIndex;
                    var pq = parent >= 0 ? global[parent] : Quaternion.Identity;
                    trace?.Mark(joint, end);
                    rotations[joint] = FromTo(
                        Model.Bones[end].Position - Model.Bones[joint].Position,
                        Vector3.Transform(direction, Quaternion.Inverse(pq))
                    );
                    foreach (int i in order)
                        Compose(i);
                }
                Correct(hip, knee, kneeTarget - positions[hip]);
                Correct(knee, ankle, target - positions[knee]);
            }
        }
        EffectiveWeights = effective;
    }

    public float[] EffectiveWeights { get; private set; } = [];

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
