using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using MmdLive.Rig;
using MmdLive.Tracking;

namespace MmdLive.Checks;

// Replays recorded observations through the live solver and writes every skin matrix and
// morph weight. Two dumps from the same observations must match after a behaviour-preserving
// refactor. Observations come from `observe`; keep them outside Git.
internal static class RigDump
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    internal static void Run(string model, string observations, string output, bool full)
    {
        var rig = new AvatarRig(model);
        long tick = Stopwatch.Frequency / 30,
            render = Stopwatch.Frequency / 60;
        using var file = File.Create(output);
        using var writer = new BinaryWriter(file);
        int frame = 0;
        var clock = new Stopwatch();
        foreach (var line in File.ReadLines(observations))
        {
            var motion = JsonSerializer.Deserialize<Motion>(line, Json)!;
            long stamp = (frame + 1) * tick;
            var sample = new InputSample(motion, stamp);
            // A 30 fps source drawn by a 60 Hz render loop: one new sample, one repeat.
            foreach (long now in new[] { stamp, stamp + render })
            {
                clock.Start();
                rig.ApplySample(sample, full, now);
                clock.Stop();
                foreach (var m in rig.Skin)
                    Write(writer, m);
                foreach (float w in rig.EffectiveWeights)
                    writer.Write(w);
            }
            frame++;
        }
        Console.WriteLine(
            $"{frame} samples, {rig.Skin.Length} bones, {clock.Elapsed.TotalMilliseconds / Math.Max(1, frame * 2):0.000} ms per solve"
        );
    }

    static void Write(BinaryWriter w, Matrix4x4 m)
    {
        w.Write(m.M11); w.Write(m.M12); w.Write(m.M13); w.Write(m.M14);
        w.Write(m.M21); w.Write(m.M22); w.Write(m.M23); w.Write(m.M24);
        w.Write(m.M31); w.Write(m.M32); w.Write(m.M33); w.Write(m.M34);
        w.Write(m.M41); w.Write(m.M42); w.Write(m.M43); w.Write(m.M44);
    }

    internal static void Compare(string a, string b)
    {
        var x = File.ReadAllBytes(a);
        var y = File.ReadAllBytes(b);
        if (x.Length != y.Length)
            throw new InvalidDataException($"dump sizes differ: {x.Length} vs {y.Length}");
        float worst = 0;
        int first = -1;
        for (int i = 0; i < x.Length; i += 4)
        {
            float d = Math.Abs(BitConverter.ToSingle(x, i) - BitConverter.ToSingle(y, i));
            if (float.IsNaN(d) || d > worst)
            {
                worst = float.IsNaN(d) ? float.PositiveInfinity : d;
                if (first < 0 && d > 1e-4f)
                    first = i / 4;
            }
        }
        Console.WriteLine($"max abs difference {worst:g4}; first float over 1e-4 at {first}");
        if (worst > 1e-4f)
            throw new InvalidDataException("rig output changed");
    }
}
