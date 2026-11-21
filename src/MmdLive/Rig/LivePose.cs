using System.Diagnostics;
using MmdLive.Tracking;

namespace MmdLive.Rig;

internal sealed partial class AvatarRig
{
    readonly RigTrace liveTrace = new();
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
    }

    // Stamps are acquisition times. Motion.Milliseconds measures inference cost, not elapsed motion.
    internal RigTrace ApplySample(InputSample sample, bool full, long now)
    {
        bool empty = sample.Stamp == 0;
        if (empty && (lastCapture == 0 || atRest))
            return liveTrace;
        if (
            !empty
            && (sample.Stamp < lastCapture || (sample.Stamp == lastCapture && lastFull == full))
        )
            return liveTrace;

        bool initial = lastCapture == 0;
        float elapsed =
            initial || (!empty && sample.Stamp == lastCapture)
                ? 1f / 30
                : (float)
                    (
                        empty
                            ? Stopwatch.GetElapsedTime(lastUpdate, now)
                            : Stopwatch.GetElapsedTime(lastCapture, sample.Stamp)
                    ).TotalSeconds;
        if (empty || elapsed > .25f || full != lastFull)
            Array.Clear(lastArm);
        if (empty && lostSince == 0)
            lostSince = now;
        atRest = empty && Stopwatch.GetElapsedTime(lostSince, now).TotalSeconds >= 2;
        // A stalled source cannot accumulate a large turn allowance for its first returning frame.
        float dt = Math.Clamp(elapsed, 0, .05f);
        bool snap = atRest || (!empty && (sample.Still || initial));
        float blend = snap ? 1 : 1 - MathF.Pow(.68f, dt * 60);
        liveTrace.Targets.Clear();
        Apply(sample.Detected, full, blend, liveTrace, snap ? 0 : dt);
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
