using System.Numerics;
using MmdLive.Tracking;

namespace MmdLive.Rig;

// Anchors the ankle while the foot is in contact with the floor. Releasing on lift permits a step.
internal sealed class Grounding
{
    sealed class Foot
    {
        public Vector2? LastPixel;
        public Vector3? Anchor;
        public int StableSamples;
        public bool Contact;
    }

    readonly Foot[] feet = [new(), new()];
    Vector2? origin;
    Motion? previous;
    Vector3 rootOffset;
    float floor;

    public void Reset()
    {
        origin = null;
        previous = null;
        rootOffset = Vector3.Zero;
        floor = 0;
        foreach (var f in feet)
        {
            f.LastPixel = null;
            f.Anchor = null;
            f.StableSamples = 0;
            f.Contact = false;
        }
    }

    public Vector3 Observe(Motion motion, float modelShoulders, float height)
    {
        if (ReferenceEquals(previous, motion))
            return rootOffset;
        previous = motion;
        if (!motion.Sees(23) || !motion.Sees(24) || !motion.Sees(11) || !motion.Sees(12))
        {
            foreach (var f in feet)
            {
                f.Contact = false;
                f.Anchor = null;
                f.LastPixel = null;
                f.StableSamples = 0;
            }
            return rootOffset;
        }
        var p = motion.Body;
        Vector2 Pixel(int i) => new(p[i].X, p[i].Y);
        float shoulderWidth = Math.Max(12, Vector2.Distance(Pixel(11), Pixel(12)));
        float scale = Math.Clamp(
            modelShoulders / shoulderWidth,
            height / (motion.Height * 4),
            height * 3 / motion.Height
        );
        Vector2 hip = (Pixel(23) + Pixel(24)) / 2;
        origin ??= hip;
        var delta = (hip - origin.Value) * scale;
        rootOffset = Vector3.Lerp(
            rootOffset,
            new Vector3(
                Math.Clamp(-delta.X, -height, height),
                Math.Clamp(-delta.Y, -height * .5f, height * .5f),
                0
            ),
            .28f
        );
        if (motion.Sees(27) || motion.Sees(28))
        {
            float low = Math.Max(motion.Sees(27) ? p[27].Y : 0, motion.Sees(28) ? p[28].Y : 0);
            floor = floor == 0 ? low : Math.Max(low, floor - shoulderWidth * .009f);
        }
        for (int i = 0; i < 2; i++)
        {
            var f = feet[i];
            var pixel = Pixel(27 + i);
            float speed = f.LastPixel is { } last
                ? Vector2.Distance(pixel, last)
                : float.PositiveInfinity;
            bool stable =
                motion.Sees(27 + i)
                && motion.Visibility[27 + i] > .57f
                && floor - pixel.Y < shoulderWidth * .22f
                && speed < shoulderWidth * .045f;
            f.StableSamples = stable ? f.StableSamples + 1 : 0;
            f.Contact = f.StableSamples >= 3;
            if (!f.Contact)
                f.Anchor = null;
            f.LastPixel = pixel;
        }
        return rootOffset;
    }

    public bool Plant(bool left, Vector3 animatedAnkle, out Vector3 target)
    {
        var foot = feet[left ? 0 : 1];
        target = animatedAnkle;
        if (!foot.Contact)
            return false;
        foot.Anchor ??= animatedAnkle;
        target = foot.Anchor.Value;
        return true;
    }
}
