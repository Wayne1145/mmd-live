using System.Numerics;
using MmdLive.Tracking;

namespace MmdLive.Rig;

internal sealed partial class AvatarRig
{
    readonly record struct ArmChain(int Shoulder, int Elbow, int Wrist);

    ArmChain[] arms = [];
    bool[] upperLimb = [];
    readonly (Vector3 Upper, Vector3 Lower)?[] lastArm = new (Vector3, Vector3)?[2];
    int[] shoulderParents = [],
        remainingBones = [];

    void PrepareArms()
    {
        arms = map.Sides.Select(side => new ArmChain(side.Arm, side.Elbow, side.Wrist)).ToArray();
        var parents = new HashSet<int>();
        foreach (var arm in arms)
            if (arm.Shoulder >= 0)
                for (
                    int i = Model.Bones[arm.Shoulder].ParentIndex;
                    i >= 0 && parents.Add(i);
                    i = Model.Bones[i].ParentIndex
                ) { }
        upperLimb = new bool[order.Length];
        foreach (int i in order)
        {
            int parent = Model.Bones[i].ParentIndex;
            upperLimb[i] =
                i == arms[0].Shoulder
                || i == arms[1].Shoulder
                || (parent >= 0 && upperLimb[parent]);
        }
        shoulderParents = order.Where(parents.Contains).ToArray();
        remainingBones = order.Where(i => !parents.Contains(i)).ToArray();
        PrepareIdleArms();
    }

    // Local rotations an unobserved arm relaxes to: upper arm hanging slightly out from the
    // body, forearm bent a little forward. Most MMD models are rigged in a T-pose, and relaxing
    // to the bind pose left a seated performer's avatar with both arms straight out.
    readonly Dictionary<int, Quaternion> idlePose = [];

    void PrepareIdleArms()
    {
        idlePose.Clear();
        foreach (var arm in arms)
        {
            if (arm.Shoulder < 0 || arm.Elbow < 0 || arm.Wrist < 0)
                continue;
            var upperRest = Model.Bones[arm.Elbow].Position - Model.Bones[arm.Shoulder].Position;
            var lowerRest = Model.Bones[arm.Wrist].Position - Model.Bones[arm.Elbow].Position;
            if (upperRest.LengthSquared() < 1e-8f || lowerRest.LengthSquared() < 1e-8f)
                continue;
            float outward = upperRest.X >= 0 ? 1 : -1;
            // Model space: +Y up, the avatar faces -Z.
            var upper = Quaternion.Normalize(
                FromTo(upperRest, Vector3.Normalize(new(outward * .3f, -1, -.06f)))
            );
            var forearm = Vector3.Normalize(new(outward * .12f, -1, -.45f));
            idlePose[arm.Shoulder] = upper;
            idlePose[arm.Elbow] = Quaternion.Normalize(
                FromTo(lowerRest, Vector3.Transform(forearm, Quaternion.Inverse(upper)))
            );
        }
    }

    static Vector2 XY(Vector3 p) => new(p.X, p.Y);

    bool HalfSpine(Motion m, out Quaternion orientation)
    {
        orientation = Quaternion.Identity;
        if (!m.Sees(11) || !m.Sees(12))
            return false;
        var span = m.Body[11] - m.Body[12];
        // A near-profile or crossed shoulder pair has insufficient horizontal baseline for roll.
        if (span.X < Math.Max(8, m.Width * .025f))
            return false;
        float roll = Math.Clamp(MathF.Atan2(span.Y, span.X), -.52f, .52f);
        float yaw = 0;
        if (m.WorldBody.Length == 33)
        {
            var axis = m.WorldBody[11] - m.WorldBody[12];
            if (axis.X > .06f)
                yaw = Math.Clamp(MathF.Atan2(axis.Z, axis.X), -.96f, .96f);
        }
        orientation =
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        return true;
    }

    // Twist the palm about its long axis using the index-to-little-finger span. One ray alone loses palm roll.
    static Quaternion PalmRotation(
        Vector3 rest,
        Vector3 restAcross,
        Vector3 live,
        Vector3 liveAcross
    )
    {
        var swing = FromTo(rest, live);
        var axis = Vector3.Normalize(live);
        var a = Vector3.Transform(restAcross, swing);
        a -= axis * Vector3.Dot(axis, a);
        var b = liveAcross - axis * Vector3.Dot(axis, liveAcross);
        if (a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f)
            return swing;
        var angle = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(a, b)), Vector3.Dot(a, b));
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, angle) * swing);
    }

    Vector3 OriginOf(int joint, Vector3[] translation)
    {
        var bone = Model.Bones[joint];
        int parent = bone.ParentIndex;
        return parent < 0
            ? bone.Position + translation[joint]
            : positions[parent]
                + Vector3.Transform(
                    bone.Position - Model.Bones[parent].Position + translation[joint],
                    global[parent]
                );
    }

    // Closest feasible elbow in the image plane, given the two fixed model bone lengths.
    static Vector2 ElbowInLens(Vector2 point, Vector2 wrist, float upper, float lower)
    {
        static Vector2 Disk(Vector2 point, Vector2 center, float radius)
        {
            var offset = point - center;
            return offset.LengthSquared() <= radius * radius
                ? point
                : center + Vector2.Normalize(offset) * radius;
        }
        var a = Disk(point, Vector2.Zero, upper);
        if (Vector2.DistanceSquared(a, wrist) <= lower * lower)
            return a;
        var b = Disk(point, wrist, lower);
        if (b.LengthSquared() <= upper * upper)
            return b;
        float distance = wrist.Length();
        if (distance < .0001f)
            return Disk(point, Vector2.Zero, Math.Min(upper, lower));
        var axis = wrist / distance;
        var normal = new Vector2(-axis.Y, axis.X);
        float along = (upper * upper - lower * lower + distance * distance) / (2 * distance);
        float height = MathF.Sqrt(Math.Max(0, upper * upper - along * along));
        return axis * along + normal * MathF.CopySign(height, Vector2.Dot(point, normal));
    }

    static Vector2 LiftDepth(
        float upperZ,
        float lowerZ,
        Vector2 prior,
        float wristPrior,
        Vector2? previous
    )
    {
        Vector2 best = default;
        float bestCost = float.PositiveInfinity;
        for (int i = 0; i < 4; i++)
        {
            var z = new Vector2((i & 1) == 0 ? upperZ : -upperZ, (i & 2) == 0 ? -lowerZ : lowerZ);
            float difference = z.X + z.Y - wristPrior;
            float cost = Vector2.DistanceSquared(z, prior) + difference * difference;
            if (previous is { } last)
                cost += .92f * Vector2.DistanceSquared(z, last);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = z;
            }
        }
        return best;
    }

    void PlaceArms(
        Motion m,
        Vector3[] translation,
        Dictionary<int, (Vector3 rest, Vector3 target)> aims,
        RigTrace? trace,
        float stepSeconds
    )
    {
        if (!m.Sees(11) || !m.Sees(12) || arms.Any(a => a.Shoulder < 0))
        {
            Array.Clear(lastArm);
            return;
        }
        var left = OriginOf(arms[0].Shoulder, translation);
        var right = OriginOf(arms[1].Shoulder, translation);
        var leftScreen = XY(m.Body[11]);
        var rightScreen = XY(m.Body[12]);
        float shoulderPixels = Vector2.Distance(leftScreen, rightScreen);
        float modelSpan = Vector2.Distance(ImageAlignment.Front(left), ImageAlignment.Front(right));
        if (
            shoulderPixels < 12
            || modelSpan < Vector3.Distance(left, right) * .25f
            || modelSpan < .001f
        )
            return;
        float units = modelSpan / shoulderPixels;
        float worldSpan =
            m.WorldBody.Length == 33 ? Vector3.Distance(m.WorldBody[11], m.WorldBody[12]) : 0;
        var imageCenter = (leftScreen + rightScreen) / 2;
        var modelCenter = (left + right) / 2;
        for (int side = 0; side < 2; side++)
        {
            var previous = lastArm[side];
            lastArm[side] = null;
            var arm = arms[side];
            if (
                arm.Elbow < 0
                || arm.Wrist < 0
                || arm.Shoulder == arm.Elbow
                || arm.Elbow == arm.Wrist
            )
                continue;
            var hand = m.Hands.FirstOrDefault(h => h.Left == (side == 0));
            int shoulder = 11 + side,
                elbow = 13 + side,
                wrist = 15 + side;
            if (hand == null && !m.Sees(wrist))
                continue;
            var handPixel = hand != null ? XY(hand.Points[0]) : XY(m.Body[wrist]);
            var origin = side == 0 ? left : right;
            var targetXY = XY(modelCenter - origin) - (handPixel - imageCenter) * units;
            if (hand != null && FaceTarget(m, hand, handPixel) is { } face)
                targetXY = Vector2.Lerp(targetXY, face.Target - XY(origin), face.Weight);
            var upperRest = Model.Bones[arm.Elbow].Position - Model.Bones[arm.Shoulder].Position;
            var lowerRest = Model.Bones[arm.Wrist].Position - Model.Bones[arm.Elbow].Position;
            float upper = upperRest.Length(),
                lower = lowerRest.Length();
            if (Math.Min(upper, lower) < .001f)
                continue;
            bool matched =
                m.Sees(wrist)
                && Vector2.Distance(handPixel, XY(m.Body[wrist])) < shoulderPixels * .28f;
            bool elbowSeen = m.Sees(elbow);
            var elbowXY = XY(modelCenter - origin) - (XY(m.Body[elbow]) - imageCenter) * units;
            bool reliableElbow =
                elbowSeen
                && (
                    hand == null
                    || matched
                    || (
                        elbowXY.Length() < upper * 1.12f
                        && (targetXY - elbowXY).Length() < lower * 1.12f
                    )
                );
            float maxReach = upper + lower - .0001f;
            if (targetXY.Length() > maxReach)
                targetXY = Vector2.Normalize(targetXY) * maxReach;
            float zLimit = MathF.Sqrt(Math.Max(0, maxReach * maxReach - targetXY.LengthSquared()));
            float depth =
                matched && worldSpan > .08f
                    ? (m.WorldBody[wrist].Z - m.WorldBody[shoulder].Z)
                        * Vector3.Distance(left, right)
                        / worldSpan
                    // Without a measured depth keep the arm's current depth. Resetting to a
                    // default threw the arm forward or back whenever hand and pose disagreed.
                    : previous is { } held
                        ? held.Upper.Z + held.Lower.Z
                        : -.22f * maxReach;
            Vector3? elbowHint = null;
            if (reliableElbow)
            {
                var e = ElbowInLens(elbowXY, targetXY, upper, lower);
                var lowerXY = targetXY - e;
                float uz = MathF.Sqrt(Math.Max(0, upper * upper - e.LengthSquared()));
                float lz = MathF.Sqrt(Math.Max(0, lower * lower - lowerXY.LengthSquared()));
                float upperPrior =
                    worldSpan > .08f
                        ? (m.WorldBody[elbow].Z - m.WorldBody[shoulder].Z)
                            * Vector3.Distance(left, right)
                            / worldSpan
                        : .12f * upper;
                Vector2? lastDepth = previous is { } armPose
                    ? new(armPose.Upper.Z, armPose.Lower.Z)
                    : null;
                var z = LiftDepth(uz, lz, new(upperPrior, depth - upperPrior), depth, lastDepth);
                // A front/back branch can change on a tiny measurement. Move wrist depth
                // continuously, then solve the elbow on the two-link circle at that depth.
                depth = z.X + z.Y;
                elbowHint = new(e, z.X);
                if (stepSeconds > 0 && previous is { } last)
                {
                    float travel = maxReach * 2.6f * stepSeconds;
                    float lastZ = last.Upper.Z + last.Lower.Z;
                    depth = lastZ + Math.Clamp(depth - lastZ, -travel, travel);
                    elbowHint = new(
                        e,
                        last.Upper.Z + Math.Clamp(z.X - last.Upper.Z, -travel, travel)
                    );
                }
            }
            var target = new Vector3(targetXY, Math.Clamp(depth, -zLimit, zLimit));
            float distance = target.Length();
            if (distance < .0001f)
                continue;
            var axis = target / distance;
            distance = Math.Clamp(distance, Math.Abs(upper - lower) + .00005f, maxReach);
            target = axis * distance;
            float along = (upper * upper - lower * lower + distance * distance) / (2 * distance);
            // A missing elbow can still articulate the mesh toward an observed wrist. Only measured parts are drawn.
            var outside = new Vector3(side == 0 ? -1 : 1, -.45f, .32f);
            var pole = elbowHint ?? previous?.Upper ?? outside;
            pole -= axis * Vector3.Dot(axis, pole);
            if (pole.LengthSquared() < .0001f)
            {
                pole = outside - axis * Vector3.Dot(axis, outside);
            }
            if (pole.LengthSquared() < .0001f)
            {
                pole = Vector3.Cross(axis, Math.Abs(axis.Y) < .8f ? Vector3.UnitY : Vector3.UnitX);
            }
            // Near full extension the bend plane is ill-conditioned. An observed elbow may
            // pass through this singularity. Slew its pole around the wrist axis on return.
            if (stepSeconds > 0 && previous is { } prior)
            {
                var before = prior.Upper - axis * Vector3.Dot(axis, prior.Upper);
                if (before.LengthSquared() > .0001f)
                {
                    float angle = MathF.Atan2(
                        Vector3.Dot(axis, Vector3.Cross(before, pole)),
                        Vector3.Dot(before, pole)
                    );
                    float limit = 12f * stepSeconds;
                    pole = Vector3.Transform(
                        before,
                        Quaternion.CreateFromAxisAngle(axis, Math.Clamp(angle, -limit, limit))
                    );
                }
            }
            var elbowDirection =
                axis * along
                + Vector3.Normalize(pole) * MathF.Sqrt(Math.Max(0, upper * upper - along * along));
            lastArm[side] = (elbowDirection, target - elbowDirection);
            aims[arm.Shoulder] = (upperRest, elbowDirection);
            aims[arm.Elbow] = (lowerRest, target - elbowDirection);
            if (reliableElbow)
            {
                trace?.Mark(arm.Shoulder, arm.Elbow);
                trace?.Mark(arm.Elbow, arm.Wrist);
            }
            else
            {
                trace?.Targets.Remove(arm.Shoulder);
                trace?.Targets.Remove(arm.Elbow);
            }
        }
    }

    // Shoulder-relative placement scales the hand by shoulder width. Heads sit at different
    // heights above the shoulders on a person and on an avatar (most of all on chibi or robot
    // models), so a hand on the performer's chin landed on the avatar's chest. Near the face,
    // measure the wrist from the chin in face heights and place it the same way from the
    // avatar's head bone, scaled by the avatar's head height. Further out, blend back.
    const float FaceNear = 1.4f,
        FaceFar = 2.6f;

    // Chin to the top of the skull is about 1.35 times chin to the forehead landmark.
    const float SkullPerFaceHeight = 1.35f;

    (Vector2 Target, float Weight)? FaceTarget(Motion m, Hand hand, Vector2 wristPixel)
    {
        int head = map.Head;
        if (m.Face.Length != FaceLandmark.Count || head < 0 || hand.Points.Length != HandLandmark.Count)
            return null;
        float headHeight = Max.Y - Model.Bones[head].Position.Y;
        var chin = XY(m.Face[FaceLandmark.Chin]);
        var forehead = XY(m.Face[FaceLandmark.Forehead]);
        float faceHeight = Vector2.Distance(chin, forehead);
        if (headHeight <= 1e-4f || faceHeight < 8)
            return null;
        var handCenter = (XY(hand.Points[HandLandmark.Wrist]) + XY(hand.Points[HandLandmark.MiddleBase])) / 2;
        float distance = Vector2.Distance(handCenter, (chin + forehead) / 2) / faceHeight;
        float t = Math.Clamp((FaceFar - distance) / (FaceFar - FaceNear), 0, 1);
        if (t <= 0)
            return null;
        float scale = headHeight / (faceHeight * SkullPerFaceHeight);
        // Image x and y both point the opposite way to model x and y, as in the shoulder mapping.
        var target = XY(positions[head]) - (wristPixel - chin) * scale;
        return (target, t * t * (3 - 2 * t));
    }

    // Small angular changes stay quiet; clear gestures can catch up within the current observation.
    static float Response(Quaternion from, Quaternion to, float baseAmount)
    {
        float error = 2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(from, to)), 0, 1));
        float movement = Math.Clamp((error - .035f) / .39f, 0, 1);
        return 1 - MathF.Pow(1 - baseAmount, .8f + 3.3f * movement);
    }
}
