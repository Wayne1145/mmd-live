using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MmdLive.Tracking;

internal readonly record struct Crop(Vector2 Center, float Side, float Angle = 0)
{
    public Vector2 Rotate(Vector2 v) =>
        new(
            v.X * MathF.Cos(Angle) - v.Y * MathF.Sin(Angle),
            v.X * MathF.Sin(Angle) + v.Y * MathF.Cos(Angle)
        );

    public Vector2 Pixel(Vector2 uv) => Center + Rotate((uv - new Vector2(.5f)) * Side);

    public static Crop Whole(Mat image) =>
        new(new(image.Width / 2f, image.Height / 2f), Math.Max(image.Width, image.Height));

    public static float Upright(Vector2 wrist, Vector2 tip) =>
        MathF.Atan2(tip.Y - wrist.Y, tip.X - wrist.X) + MathF.PI / 2;
}

// One ONNX model on DirectML. Reuses its crop buffers, so one instance serves one thread.
internal sealed class GpuModel : IDisposable
{
    readonly InferenceSession session;
    readonly string input;
    readonly int[] shape;
    readonly bool nchw;
    readonly Mat pixels = new();
    readonly float[] affine = new float[6];
    float[] values = [];
    internal int Size { get; }
    internal string Name { get; }

    // Cumulative cost for diagnostics: image preparation on the CPU and the inference call.
    internal int Calls { get; private set; }
    internal double PrepareMilliseconds { get; private set; }
    internal double InferMilliseconds { get; private set; }

    public GpuModel(string path, int adapter)
    {
        Name = Path.GetFileNameWithoutExtension(path);
        using var options = new SessionOptions
        {
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        // A symbolic batch dimension stops DirectML from compiling one fixed graph. The face
        // mesh then runs as many small dispatches and costs ~6x more on a laptop GPU.
        foreach (string symbol in SymbolicInputDimensions(path))
            options.AddFreeDimensionOverrideByName(symbol, 1);
        options.AppendExecutionProvider_DML(adapter);
        options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        try
        {
            session = new InferenceSession(path, options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"DirectML 初始化 {Path.GetFileName(path)} 失败（GPU {adapter}）。{ex.Message}",
                ex
            );
        }
        var meta = session.InputMetadata.Single();
        input = meta.Key;
        shape = meta.Value.Dimensions.Select(x => x < 0 ? 1 : x).ToArray();
        nchw = shape.Length == 4 && shape[1] == 3;
        Size = shape.Length == 4 ? shape[2] : 0;
    }

    static string[] SymbolicInputDimensions(string path)
    {
        using var probe = new InferenceSession(path);
        return probe
            .InputMetadata.Values.SelectMany(v => v.SymbolicDimensions)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToArray();
    }

    public float[][] Run(float[] values, params string[] outputs)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var tensor = new DenseTensor<float>(values, shape);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(input, tensor) };
        using var results =
            outputs.Length == 0 ? session.Run(inputs) : session.Run(inputs, outputs);
        var arrays = results.Select(x => x.AsTensor<float>().ToArray()).ToArray();
        Calls++;
        InferMilliseconds += System
            .Diagnostics.Stopwatch.GetElapsedTime(start)
            .TotalMilliseconds;
        return arrays;
    }

    public unsafe float[][] Image(Mat frame, Crop roi, bool signed, params string[] outputs)
    {
        // The conversion below reads packed 3-byte BGR pixels.
        if (frame.Type() != MatType.CV_8UC3)
            throw new ArgumentException($"追踪输入必须是 8 位 BGR 图像，实际为 {frame.Type()}");
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var origin = roi.Pixel(new(.5f / Size, .5f / Size)) - new Vector2(.5f);
        var dx = roi.Rotate(new(roi.Side / Size, 0));
        var dy = roi.Rotate(new(0, roi.Side / Size));
        (affine[0], affine[1], affine[2]) = (dx.X, dy.X, origin.X);
        (affine[3], affine[4], affine[5]) = (dx.Y, dy.Y, origin.Y);
        using (var transform = Mat.FromPixelData(2, 3, MatType.CV_32FC1, affine))
            Cv2.WarpAffine(
                frame,
                pixels,
                transform,
                new OpenCvSharp.Size(Size, Size),
                InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
                BorderTypes.Constant,
                Scalar.All(signed ? 127.5 : 0)
            );
        if (!pixels.IsContinuous())
            throw new InvalidOperationException("裁切缓冲区不连续");
        var area = Size * Size;
        if (values.Length != area * 3)
            values = new float[area * 3];
        var value = values;
        var ptr = (byte*)pixels.DataPointer;
        float scale = signed ? 2f / 255 : 1f / 255,
            bias = signed ? -1 : 0;
        for (int i = 0; i < area; i++)
        for (int c = 0; c < 3; c++)
            value[nchw ? c * area + i : i * 3 + c] = ptr[i * 3 + 2 - c] * scale + bias;
        PrepareMilliseconds += System
            .Diagnostics.Stopwatch.GetElapsedTime(start)
            .TotalMilliseconds;
        return Run(value, outputs);
    }

    public void Dispose()
    {
        session.Dispose();
        pixels.Dispose();
    }
}

internal sealed record Detection(Vector2 Center, Vector2 Size, Vector2[] Marks, float Score);

internal static class BlazeDetector
{
    static readonly Dictionary<int, Vector2[]> Anchors = new()
    {
        [128] = Grid((16, 2), (8, 6)),
        [192] = Grid((24, 2), (12, 6)),
        [224] = Grid((28, 2), (14, 2), (7, 6)),
    };

    static Vector2[] Grid(params (int side, int repeats)[] grids)
    {
        var a = new List<Vector2>();
        foreach (var (side, repeats) in grids)
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            for (int n = 0; n < repeats; n++)
                a.Add(new((x + .5f) / side, (y + .5f) / side));
        return a.ToArray();
    }

    public static List<Detection> Find(
        GpuModel model,
        Mat frame,
        Crop sample,
        bool signed,
        float threshold,
        int max
    )
    {
        var raw = model.Image(frame, sample, signed);
        var reg = raw.MaxBy(x => x.Length)!;
        var scores = raw.MinBy(x => x.Length)!;
        var anchors = Anchors[model.Size];
        int step = reg.Length / scores.Length;
        var candidates = new List<Detection>();
        for (int i = 0; i < scores.Length; i++)
        {
            float score = Sigmoid(scores[i]);
            if (score < threshold)
                continue;
            int j = i * step;
            Vector2 Read(int k) =>
                sample.Pixel(anchors[i] + new Vector2(reg[j + k], reg[j + k + 1]) / model.Size);
            var marks = new Vector2[(step - 4) / 2];
            for (int k = 0; k < marks.Length; k++)
                marks[k] = Read(4 + 2 * k);
            candidates.Add(
                new(
                    Read(0),
                    new Vector2(reg[j + 2], reg[j + 3]) * (sample.Side / model.Size),
                    marks,
                    score
                )
            );
        }
        var result = new List<Detection>();
        foreach (var d in candidates.OrderByDescending(x => x.Score))
        {
            if (result.Any(o => Overlap(o, d) > .3f))
                continue;
            result.Add(d);
            if (result.Count == max)
                break;
        }
        return result;
    }

    static float Overlap(Detection a, Detection b)
    {
        var extent = Vector2.Max(
            Vector2.Zero,
            Vector2.Min(a.Center + a.Size / 2, b.Center + b.Size / 2)
                - Vector2.Max(a.Center - a.Size / 2, b.Center - b.Size / 2)
        );
        var intersect = extent.X * extent.Y;
        return intersect / Math.Max(.01f, a.Size.X * a.Size.Y + b.Size.X * b.Size.Y - intersect);
    }

    public static float Sigmoid(float x) => 1 / (1 + MathF.Exp(-Math.Clamp(x, -70, 70)));
}
