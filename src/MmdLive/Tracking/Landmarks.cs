namespace MmdLive.Tracking;

// MediaPipe BlazePose landmark indices. "Left" is the performer's own left side; add 1 for
// the right side of any paired landmark.
internal static class PoseLandmark
{
    public const int Count = 33;
    public const int Nose = 0;
    public const int LeftEye = 2;
    public const int RightEye = 5;
    public const int LeftEar = 7;
    public const int RightEar = 8;
    public const int LeftShoulder = 11;
    public const int RightShoulder = 12;
    public const int LeftElbow = 13;
    public const int LeftWrist = 15;
    public const int LeftPinky = 17;
    public const int LeftIndex = 19;
    public const int LeftHip = 23;
    public const int RightHip = 24;
    public const int LeftKnee = 25;
    public const int LeftAnkle = 27;
    public const int RightAnkle = 28;
    public const int LeftHeel = 29;
    public const int LeftFootIndex = 31;
}

// MediaPipe face mesh indices with iris refinement. Image-left is the performer's right.
internal static class FaceLandmark
{
    public const int Count = 478;
    public const int NoseTip = 1;
    public const int Forehead = 10;
    public const int Chin = 152;
    public const int RightEyeOuter = 33;
    public const int RightEyeInner = 133;
    public const int RightEyeTop = 159;
    public const int RightEyeBottom = 145;
    public const int LeftEyeOuter = 263;
    public const int LeftEyeInner = 362;
    public const int LeftEyeTop = 386;
    public const int LeftEyeBottom = 374;
    public const int RightIris = 468;
    public const int LeftIris = 473;
}

// MediaPipe hand landmarks. Finger joints are 1 + 4 * finger + segment.
internal static class HandLandmark
{
    public const int Count = 21;
    public const int Wrist = 0;
    public const int IndexBase = 5;
    public const int MiddleBase = 9;
    public const int RingBase = 13;
    public const int LittleBase = 17;

    public static int Joint(int finger, int segment) => 1 + 4 * finger + segment;
}

// MediaPipe face blendshape output order.
internal static class Blendshape
{
    public const int Count = 52;
    public const int BrowDownLeft = 1;
    public const int BrowDownRight = 2;
    public const int BrowInnerUp = 3;
    public const int EyeBlinkLeft = 9;
    public const int EyeBlinkRight = 10;
    public const int JawOpen = 25;
    public const int MouthFunnel = 32;
    public const int MouthPucker = 38;
    public const int MouthSmileLeft = 44;
    public const int MouthSmileRight = 45;
}
