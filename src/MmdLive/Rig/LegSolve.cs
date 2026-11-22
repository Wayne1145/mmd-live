using System.Numerics;
using MmdLive.Tracking;

namespace MmdLive.Rig;

internal sealed partial class AvatarRig
{
    // Full body only: move the root with the hips so the avatar can step around the frame.
    void GroundRoot(Motion m, bool full, RigTrace? trace)
    {
        if (!full || m.Body.Length != PoseLandmark.Count)
        {
            grounding.Reset();
            return;
        }
        int l = map.Sides[0].Arm,
            r = map.Sides[1].Arm,
            center = map.Center;
        float height = Max.Y - Min.Y;
        float shoulders =
            l >= 0 && r >= 0
                ? Vector3.Distance(Model.Bones[l].Position, Model.Bones[r].Position)
                : height * .22f;
        var delta = grounding.Observe(m, shoulders, height);
        if (center >= 0)
        {
            translation[center] += delta;
            if (m.Sees(PoseLandmark.LeftHip) && m.Sees(PoseLandmark.RightHip))
                trace?.Mark(center);
        }
    }

    // Two-bone IK that pins a planted foot to its anchor while the root moves.
    void PlantFeet(Motion m, bool full, RigTrace? trace)
    {
        if (!full || m.Body.Length != PoseLandmark.Count)
            return;
        for (int side = 0; side < 2; side++)
        {
            var b = map.Sides[side];
            int hip = b.Leg,
                knee = b.Knee,
                ankle = b.Ankle;
            if (
                hip < 0
                || knee < 0
                || ankle < 0
                || !m.Sees(PoseLandmark.LeftHip + side)
                || !m.Sees(PoseLandmark.LeftKnee + side)
                || !m.Sees(PoseLandmark.LeftAnkle + side)
                || !grounding.Plant(side == 0, positions[ankle], out var target)
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
            float span = Math.Clamp(length, Math.Abs(upper - lower) + .0001f, upper + lower - .0001f);
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
                + Vector3.Normalize(bend) * MathF.Sqrt(Math.Max(0, upper * upper - along * along));
            Correct(hip, knee, kneeTarget - positions[hip], trace);
            Correct(knee, ankle, target - positions[knee], trace);
        }
    }

    void Correct(int joint, int end, Vector3 direction, RigTrace? trace)
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
}
