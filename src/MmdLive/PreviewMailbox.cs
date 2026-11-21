using System.Drawing.Imaging;
using MmdLive.Tracking;
using OpenCvSharp;

namespace MmdLive;

internal sealed record InputSample(Motion Detected, long Stamp, bool Still = false)
{
    public bool Expired(long now) =>
        !Still
        && Stamp != 0
        && System.Diagnostics.Stopwatch.GetElapsedTime(Stamp, now).TotalMilliseconds > 500;

    public static readonly InputSample Empty = new(Motion.Empty, 0);
}

// A Bitmap has exactly one owner: the pending slot or the UI. A slow UI skips whole frames.
internal sealed class PreviewMailbox : IDisposable
{
    readonly object gate = new();
    InputSample latest = InputSample.Empty;
    Bitmap? pending;
    bool disposed;

    public InputSample Latest
    {
        get
        {
            lock (gate)
                return latest;
        }
    }

    public void Publish(InputSample sample, Bitmap? image)
    {
        lock (gate)
        {
            if (disposed)
            {
                image?.Dispose();
                return;
            }
            pending?.Dispose();
            pending = image;
            latest = sample;
        }
    }

    public InputSample Read(out Bitmap? image)
    {
        lock (gate)
        {
            image = pending;
            pending = null;
            return latest;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending?.Dispose();
            pending = null;
            latest = InputSample.Empty;
        }
    }

    public static Bitmap Downsample(Mat bgr, int maxSide = 720)
    {
        float scale = Math.Min(1f, (float)maxSide / Math.Max(bgr.Width, bgr.Height));
        int w = Math.Max(1, (int)MathF.Round(bgr.Width * scale));
        int h = Math.Max(1, (int)MathF.Round(bgr.Height * scale));
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        try
        {
            var bits = bitmap.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                bitmap.PixelFormat
            );
            try
            {
                using var target = Mat.FromPixelData(
                    h,
                    w,
                    MatType.CV_8UC3,
                    bits.Scan0,
                    bits.Stride
                );
                Cv2.Resize(
                    bgr,
                    target,
                    new OpenCvSharp.Size(w, h),
                    interpolation: InterpolationFlags.Linear
                );
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
