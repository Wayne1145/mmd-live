using System.Diagnostics;

namespace MmdLive.Tracking;

internal sealed class OcclusionHold
{
    readonly Dictionary<bool, (Hand hand, long seen)> hands = [];
    Motion previous = Motion.Empty;
    long faceSeen,
        bodySeen;

    public Motion Update(Motion sample)
    {
        long now = Stopwatch.GetTimestamp();
        bool Recent(long time) =>
            time > 0 && Stopwatch.GetElapsedTime(time, now).TotalMilliseconds < 180;
        if (sample.Face.Length > 0)
            faceSeen = now;
        else if (Recent(faceSeen))
            sample = sample with { Face = previous.Face, Expressions = previous.Expressions };
        if (sample.Body.Length > 0)
            bodySeen = now;
        else if (Recent(bodySeen))
            sample = sample with
            {
                Body = previous.Body,
                Visibility = previous.Visibility,
                WorldBody = previous.WorldBody,
            };
        foreach (var hand in sample.Hands)
            hands[hand.Left] = (hand, now);
        sample = sample with
        {
            Hands = hands.Values.Where(h => Recent(h.seen)).Select(h => h.hand).ToArray(),
        };
        previous = sample;
        return sample;
    }
}
