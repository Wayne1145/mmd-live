using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MmdLive.Tracking;

namespace MmdLive;

internal sealed class PlaybackMetrics(string directory, int seconds)
{
    readonly List<double> latency = [];
    readonly List<double> inference = [];
    long first,
        frames,
        updates,
        nextWrite;
    Motion last = Motion.Empty;

    internal bool Record(Motion motion, long captured)
    {
        if (captured == 0 || motion == Motion.Empty)
            return false;
        long now = Stopwatch.GetTimestamp();
        if (first == 0)
        {
            first = now;
            nextWrite = now + Stopwatch.Frequency * 5;
        }
        frames++;
        if (!ReferenceEquals(last, motion))
        {
            last = motion;
            updates++;
            latency.Add(Stopwatch.GetElapsedTime(captured, now).TotalMilliseconds);
            inference.Add(motion.Milliseconds);
        }
        double elapsed = Stopwatch.GetElapsedTime(first, now).TotalSeconds;
        bool finished = elapsed >= seconds;
        if (now >= nextWrite || finished)
        {
            nextWrite = now + Stopwatch.Frequency * 10;
            Directory.CreateDirectory(directory);
            static double P95(List<double> x)
            {
                var values = x.ToArray();
                Array.Sort(values);
                return values.Length == 0 ? 0 : values[(int)Math.Floor(.95 * (values.Length - 1))];
            }
            var report = new
            {
                elapsedSeconds = elapsed,
                frames,
                updates,
                renderFps = frames / Math.Max(.01, elapsed),
                trackingFps = updates / Math.Max(.01, elapsed),
                latencyP95Ms = P95(latency),
                inferenceP95Ms = P95(inference),
                inputWidth = motion.Width,
                inputHeight = motion.Height,
                finished,
            };
            string target = Path.Combine(directory, "playback.json");
            File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(report, Program.Json));
            File.Move(target + ".tmp", target, true);
        }
        return finished;
    }
}

internal static class RenderClock
{
    [StructLayout(LayoutKind.Sequential)]
    struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public System.Drawing.Point Position;
        public uint Private;
    }

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out Message msg, nint hwnd, uint first, uint last, uint flags);

    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjectsEx(
        uint count,
        nint handles,
        uint ms,
        uint wakeMask,
        uint flags
    );

    internal static bool HasMessages() => PeekMessage(out _, 0, 0, 0, 0);

    internal static void Wait(int ms) => MsgWaitForMultipleObjectsEx(0, 0, (uint)ms, 0x4ff, 4);
}
