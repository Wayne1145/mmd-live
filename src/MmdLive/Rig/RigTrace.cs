using System.Numerics;
using Coocoo3D.Extensions.FileFormat;
using MmdLive.Tracking;

namespace MmdLive.Rig;

internal readonly record struct DrivenBone(string Name, Vector3 Start, Vector3 End);

internal readonly record struct ImageAlignment(
    Vector2 Origin,
    Vector2 ModelOrigin,
    float Scale,
    string Anchor
)
{
    internal static Vector2 Front(Vector3 p) => new(-p.X, -p.Y);

    public Vector2 Pixel(Vector3 p) => Origin + (Front(p) - ModelOrigin) * Scale;
}

internal sealed record RigSnapshot(DrivenBone[] Bones, ImageAlignment? Alignment);

// Written only by solver paths that actually resolved and drove a bone in this frame.
internal sealed class RigTrace
{
    internal readonly Dictionary<int, (int End, Vector3? Offset)> Targets = [];

    public void Mark(int bone, int end = -1, Vector3? offset = null)
    {
        if (bone >= 0 && (end >= 0 || offset.HasValue || !Targets.ContainsKey(bone)))
            Targets[bone] = (end, offset);
    }
}

internal sealed partial class AvatarRig
{
    public RigSnapshot Snapshot(RigTrace trace, Motion detected)
    {
        var result = new DrivenBone[trace.Targets.Count];
        int index = 0;
        foreach (var (joint, target) in trace.Targets)
        {
            var bone = Model.Bones[joint];
            int end = target.End;
            if (end < 0 && target.Offset == null && (bone.Flags & PMX_BoneFlag.ChildUseId) != 0)
                end = bone.ChildId;
            var tip =
                (uint)end < (uint)positions.Length
                    ? positions[end]
                    : positions[joint]
                        + Vector3.Transform(target.Offset ?? bone.ChildOffset, global[joint]);
            result[index++] = new(bone.Name, positions[joint], tip);
        }
        return new(result, result.Length > 0 ? Align(detected) : null);
    }

    // Orthographic registration for comparing directions on the input. It does not fit or rotate the pose.
    ImageAlignment? Align(Motion m)
    {
        ImageAlignment? Pair(int a, int b, Vector3 sourceA, Vector3 sourceB, string label)
        {
            if (a < 0 || b < 0)
                return null;
            var start = ImageAlignment.Front(positions[a]);
            var end = ImageAlignment.Front(positions[b]);
            float span = Vector2.Distance(start, end);
            var x = new Vector2(sourceA.X, sourceA.Y);
            var y = new Vector2(sourceB.X, sourceB.Y);
            float pixels = Vector2.Distance(x, y);
            if (
                span < Math.Max(.0001f, Vector3.Distance(positions[a], positions[b]) * .25f)
                || pixels < 6
            )
                return null;
            return new((x + y) / 2, (start + end) / 2, pixels / span, label);
        }
        bool Visible(int a, int b) =>
            m.Body.Length == 33 && m.Visibility[a] > .5f && m.Visibility[b] > .5f;
        if (
            Visible(11, 12)
            && Pair(
                Bone("左腕", "leftarm", "leftupperarm"),
                Bone("右腕", "rightarm", "rightupperarm"),
                m.Body[11],
                m.Body[12],
                "肩宽"
            )
                is { } shoulders
        )
            return shoulders;
        if (
            m.Face.Length == 478
            && Pair(
                Bone("左目", "lefteye"),
                Bone("右目", "righteye"),
                m.Face[473],
                m.Face[468],
                "眼距"
            )
                is { } eyes
        )
            return eyes;
        if (
            Visible(23, 24)
            && Pair(
                Bone("左足", "leftleg", "leftupperleg"),
                Bone("右足", "rightleg", "rightupperleg"),
                m.Body[23],
                m.Body[24],
                "髋宽"
            )
                is { } hips
        )
            return hips;
        foreach (var hand in m.Hands)
        {
            string side = hand.Left ? "左" : "右",
                en = hand.Left ? "left" : "right";
            if (
                Pair(
                    Bone(side + "手首", en + "wrist", en + "hand"),
                    Bone(side + "中指1", en + "middle1"),
                    hand.Points[0],
                    hand.Points[9],
                    side + "手掌"
                ) is
                { } palm
            )
                return palm;
        }
        return null;
    }
}
