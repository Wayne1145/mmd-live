using System.Diagnostics;
using MmdLive.Tracking;

namespace MmdLive.Rig;

internal sealed partial class AvatarRig
{
    readonly RigTrace liveTrace = new();
    readonly MotionFilter filter = new();
    // The filtered form of the latest sample. Render frames between samples reuse this object.
    Motion filtered = Motion.Empty;
    long lastCapture,
        lastUpdate,
        lostSince;
    bool lastFull,
        atRest;

    internal void ResetLiveTracking()
    {
        lastCapture = lastUpdate = lostSince = 0;
        atRest = false;
        liveTrace.Targets.Clear();
        Array.Clear(lastArm);
        grounding.Reset();
        filter.Reset();
        filtered = Motion.Empty;
    }

    // Stamps are acquisition times. Motion.Milliseconds measures inference cost, not elapsed motion.
    // Every render frame steps the pose towards the latest sample. A 10-15 fps camera then
    // eases between observations instead of holding still and jumping on each new frame.
    internal RigTrace ApplySample(InputSample sample, bool full, long now)
    {
        bool empty = sample.Stamp == 0;
        if (empty && (lastCapture == 0 || atRest))
            return liveTrace;
        if (!empty && sample.Stamp < lastCapture)
            return liveTrace;
        bool repeat = !empty && sample.Stamp == lastCapture;
        // A still image is solved once with a snap. Re-rendering it cannot change the pose.
        if (repeat && sample.Still && lastFull == full)
            return liveTrace;

        bool initial = lastCapture == 0;
        // Time advanced since the previous solve, measured on the render clock.
        float elapsed = initial
            ? 1f / 30
            : (float)Stopwatch.GetElapsedTime(lastUpdate, now).TotalSeconds;
        if (repeat && lastFull == full && elapsed <= 0)
            return liveTrace;
        // A gap in the source itself, not the render cadence, invalidates the previous arm branch.
        float sourceGap =
            empty || repeat || initial
                ? 0
                : (float)Stopwatch.GetElapsedTime(lastCapture, sample.Stamp).TotalSeconds;
        if (empty || sourceGap > .25f || full != lastFull)
            Array.Clear(lastArm);
        if (empty && lostSince == 0)
            lostSince = now;
        atRest = empty && Stopwatch.GetElapsedTime(lostSince, now).TotalSeconds >= 2;
        // A stalled source cannot accumulate a large turn allowance for its first returning frame.
        float dt = Math.Clamp(elapsed, 0, .05f);
        bool snap = atRest || (!empty && (sample.Still || initial));
        float blend = snap ? 1 : 1 - MathF.Pow(.68f, dt * 60);
        // Each camera sample passes through the landmark filter once. A still image or a
        // returning source starts the filter fresh.
        if (empty || sample.Still || sourceGap > .25f)
            filter.Reset();
        if (empty || sample.Still)
            filtered = sample.Detected;
        else if (!repeat)
            filtered = filter.Step(sample.Detected, initial ? 1f / 30 : sourceGap);
        liveTrace.Targets.Clear();
        Apply(filtered, full, blend, liveTrace, snap ? 0 : dt);
        if (!empty)
        {
            lastCapture = sample.Stamp;
            lostSince = 0;
        }
        lastFull = full;
        lastUpdate = now;
        return liveTrace;
    }
}
