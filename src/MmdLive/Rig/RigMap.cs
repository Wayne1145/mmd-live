namespace MmdLive.Rig;

// Bone and morph indices resolved once per model or binding change. The solver runs every
// render frame, so it must not normalize names or concatenate strings in its hot path.
internal sealed class SideBones
{
    public int Arm,
        Elbow,
        Wrist,
        Leg,
        Knee,
        Ankle,
        Toe,
        Eye;

    // [finger, segment] for thumb, index, middle, ring, little. -1 when absent.
    public readonly int[,] Fingers = new int[5, 3];

    public int Finger(Digit digit, int segment) => Fingers[(int)digit, segment];
}

internal enum Digit
{
    Thumb,
    Index,
    Middle,
    Ring,
    Little,
}

internal sealed class BoneMap
{
    public int Center,
        Spine,
        Neck,
        Head;

    // [0] is the performer's and the avatar's left side, [1] the right.
    public readonly SideBones[] Sides = [new(), new()];
}

internal sealed class MorphMap
{
    public int BlinkLeft,
        BlinkRight,
        Blink,
        A,
        I,
        U,
        O,
        E,
        Smile,
        BrowUp,
        BrowDown;

    public bool SeparateBlink => BlinkLeft >= 0 && BlinkRight >= 0;
}

internal sealed partial class AvatarRig
{
    // Keep both sides symmetric: separate-eye blinking requires a match on each side.
    static readonly string[] LeftBlink =
    [
        "ウィンク",
        "ウィンク左",
        "まばたき左",
        "winkleft",
        "blinkleft",
        "eyeblinkleft",
    ];
    static readonly string[] RightBlink =
    [
        "ウィンク右",
        "まばたき右",
        "winkright",
        "blinkright",
        "eyeblinkright",
    ];
    static readonly string[] FingerJp = ["親指", "人指", "中指", "薬指", "小指"];
    static readonly string[] FingerEn = ["thumb", "index", "middle", "ring", "little"];

    readonly BoneMap map = new();
    readonly MorphMap expressions = new();

    void ResolveMap()
    {
        map.Center = Bone("センター", "center", "下半身", "hips");
        map.Spine = Bone("上半身", "spine");
        map.Neck = Bone("首", "neck", "上半身2");
        map.Head = Bone("頭", "head");
        for (int side = 0; side < 2; side++)
        {
            string s = side == 0 ? "左" : "右",
                e = side == 0 ? "left" : "right";
            var b = map.Sides[side];
            b.Arm = Bone(s + "腕", e + "arm", e + "upperarm");
            b.Elbow = Bone(s + "ひじ", s + "肘", e + "elbow", e + "lowerarm");
            b.Wrist = Bone(s + "手首", e + "wrist", e + "hand");
            b.Leg = Bone(s + "足", e + "leg", e + "upperleg");
            b.Knee = Bone(s + "ひざ", s + "膝", e + "knee", e + "lowerleg");
            b.Ankle = Bone(s + "足首", e + "ankle", e + "foot");
            b.Toe = Bone(s + "つま先", e + "toe");
            b.Eye = Bone(s + "目", e + "eye");
            for (int digit = 0; digit < 5; digit++)
            for (int segment = 0; segment < 3; segment++)
            {
                // MMD numbers thumb segments from 0 and the other fingers from 1.
                string n = (segment + (digit == 0 ? 0 : 1)).ToString();
                b.Fingers[digit, segment] = Bone(
                    s + FingerJp[digit] + n,
                    s + (digit == 1 ? "人差指" : FingerJp[digit]) + n,
                    e + FingerEn[digit] + (segment + 1),
                    s + FingerJp[digit] + (segment == 0 ? "根" : "先")
                );
            }
        }
        int Morph(params string[] names) => Lookup(morphs, names);
        expressions.BlinkLeft = Morph(LeftBlink);
        expressions.BlinkRight = Morph(RightBlink);
        expressions.Blink = Morph("まばたき", "瞬き", "blink", "eyesclosed");
        expressions.A = Morph("あ", "a", "jawopen");
        expressions.I = Morph("い", "i");
        expressions.U = Morph("う", "u", "pucker");
        expressions.O = Morph("お", "o", "funnel");
        expressions.E = Morph("え", "e");
        expressions.Smile = Morph("にやり", "口角上げ", "smile");
        expressions.BrowUp = Morph("上", "browup");
        expressions.BrowDown = Morph("怒り", "browdown");
    }
}
