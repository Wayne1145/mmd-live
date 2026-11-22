using System.Numerics;
using MmdLive.Tracking;

namespace MmdLive.Rig;

internal sealed partial class AvatarRig
{
    // Blendshapes rarely reach 1 on a real face. A small gain lets a full blink close the eye.
    const float ExpressionGain = 1.18f;

    // Head orientation in camera space, expression morphs and eye gaze. Returns null when no
    // face mesh was found this frame.
    Quaternion? SolveFace(Motion m, RigTrace? trace)
    {
        if (m.Face.Length != FaceLandmark.Count)
            return null;
        var f = m.Face;
        var eyes = f[FaceLandmark.LeftEyeOuter] - f[FaceLandmark.RightEyeOuter];
        var vertical = f[FaceLandmark.Chin] - f[FaceLandmark.Forehead];
        var yaw = MathF.Atan2(eyes.Z, Math.Max(1, Math.Abs(eyes.X)));
        var pitch = -MathF.Atan2(vertical.Z, Math.Max(1, Math.Abs(vertical.Y)));
        var roll = MathF.Atan2(eyes.Y, eyes.X);
        var headInCamera = Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        trace?.Mark(map.Head);

        float E(int i) =>
            m.Expressions.Length == Blendshape.Count
                ? Math.Clamp(
                    (m.Expressions[i] - (neutral.Length == Blendshape.Count ? neutral[i] : 0))
                        * ExpressionGain,
                    0,
                    1
                )
                : 0;
        float blinkL = E(Blendshape.EyeBlinkLeft),
            blinkR = E(Blendshape.EyeBlinkRight),
            smileL = E(Blendshape.MouthSmileLeft),
            smileR = E(Blendshape.MouthSmileRight);
        if (expressions.SeparateBlink)
        {
            Want(expressions.BlinkLeft, blinkL);
            Want(expressions.BlinkRight, blinkR);
        }
        else
            Want(expressions.Blink, Math.Max(blinkL, blinkR));
        Want(expressions.A, E(Blendshape.JawOpen));
        Want(expressions.I, smileL * .55f + smileR * .45f);
        Want(expressions.U, E(Blendshape.MouthPucker));
        Want(expressions.O, E(Blendshape.MouthFunnel));
        Want(expressions.E, (smileL + smileR) * .3f);
        Want(expressions.Smile, (smileL + smileR) * .5f);
        Want(expressions.BrowUp, E(Blendshape.BrowInnerUp));
        Want(expressions.BrowDown, (E(Blendshape.BrowDownLeft) + E(Blendshape.BrowDownRight)) * .5f);

        // The mesh's left eye (image right) drives the avatar's left eye bone.
        Gaze(
            f,
            map.Sides[0].Eye,
            FaceLandmark.LeftIris,
            FaceLandmark.LeftEyeInner,
            FaceLandmark.LeftEyeOuter,
            FaceLandmark.LeftEyeTop,
            FaceLandmark.LeftEyeBottom,
            trace
        );
        Gaze(
            f,
            map.Sides[1].Eye,
            FaceLandmark.RightIris,
            FaceLandmark.RightEyeInner,
            FaceLandmark.RightEyeOuter,
            FaceLandmark.RightEyeTop,
            FaceLandmark.RightEyeBottom,
            trace
        );
        return headInCamera;
    }

    void Want(int morph, float amount)
    {
        if (morph >= 0)
            want[morph] = Math.Max(want[morph], amount);
    }

    // Iris offset inside the eye opening, mapped to a small yaw and pitch of the eye bone.
    void Gaze(
        Vector3[] f,
        int bone,
        int iris,
        int inner,
        int outer,
        int top,
        int bottom,
        RigTrace? trace
    )
    {
        var center = (f[inner] + f[outer]) / 2;
        float w = Math.Max(2, Math.Abs(f[inner].X - f[outer].X));
        float h = Math.Max(w * .23f, Math.Abs(f[top].Y - f[bottom].Y));
        float x = Math.Clamp((f[iris].X - center.X) / w, -.25f, .25f),
            y = Math.Clamp((f[iris].Y - (f[top].Y + f[bottom].Y) / 2) / h, -.4f, .4f);
        Rotate(bone, Quaternion.CreateFromYawPitchRoll(x * 1.8f, -y * .6f, 0), trace);
    }
}
