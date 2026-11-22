using MmdLive.Tracking;

namespace MmdLive.Rig;

internal sealed partial class AvatarRig
{
    // Palm orientation from the wrist, middle, index and little bases; then each finger segment.
    void AimHands(Motion m, RigTrace? frameTrace)
    {
        foreach (var h in m.Hands)
        {
            // The preview draws only what this frame observed. A held hand drives silently.
            var trace = h.Held ? null : frameTrace;
            var b = map.Sides[h.Left ? 0 : 1];
            var p = h.Points;
            int wrist = b.Wrist,
                middle = b.Finger(Digit.Middle, 0),
                index = b.Finger(Digit.Index, 0),
                little = b.Finger(Digit.Little, 0);
            var palmWrist = p[HandLandmark.Wrist];
            var palmMiddle = p[HandLandmark.MiddleBase];
            // The tracker keeps a hand whose wrist is just below the frame when its palm is in
            // view. Its extrapolated wrist still orients the palm.
            bool wristSeen = m.Sees(palmWrist) || (h.Points.Length == HandLandmark.Count && float.IsFinite(palmWrist.X) && float.IsFinite(palmWrist.Y));
            if (
                wrist >= 0
                && middle >= 0
                && index >= 0
                && little >= 0
                && wristSeen
                && m.Sees(palmMiddle)
                && m.Sees(p[HandLandmark.IndexBase])
                && m.Sees(p[HandLandmark.LittleBase])
                && (palmMiddle - palmWrist).LengthSquared() > .001f
            )
            {
                orientations[wrist] = PalmRotation(
                    Model.Bones[middle].Position - Model.Bones[wrist].Position,
                    Model.Bones[index].Position - Model.Bones[little].Position,
                    ToModel(palmMiddle - palmWrist),
                    ToModel(p[HandLandmark.IndexBase] - p[HandLandmark.LittleBase])
                );
                trace?.Mark(wrist, middle);
            }
            else if (wristSeen && m.Sees(palmMiddle))
                Aim(wrist, middle, palmMiddle - palmWrist, trace);
            for (int finger = 0; finger < 5; finger++)
            for (int part = 0; part < 3; part++)
            {
                int joint = b.Fingers[finger, part],
                    next = part < 2 ? b.Fingers[finger, part + 1] : -1;
                if (joint < 0)
                    continue;
                int k = HandLandmark.Joint(finger, part);
                if (!m.Sees(p[k]) || !m.Sees(p[k + 1]))
                    continue;
                var direction = p[k + 1] - p[k];
                if (next >= 0)
                    Aim(joint, next, direction, trace);
                else
                {
                    // A fingertip bone has no child to aim at. Aim its offset from the previous joint.
                    int prev = b.Fingers[finger, Math.Max(0, part - 1)];
                    if (prev >= 0 && prev != joint)
                    {
                        var tail = Model.Bones[joint].Position - Model.Bones[prev].Position;
                        absolute[joint] = (tail, ToModel(direction));
                        trace?.Mark(joint, offset: tail);
                    }
                }
            }
        }
    }
}
