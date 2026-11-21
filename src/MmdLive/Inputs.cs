using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MmdLive.Tracking;
using OpenCvSharp;

namespace MmdLive;

internal sealed record StampedFrame(Mat Image, long Stamp);

internal enum SourceKind
{
    Image,
    Video,
    Camera,
}

internal sealed record Source(SourceKind Kind, string Path, int CameraIndex = 0);

internal static class CameraDevices
{
    [DllImport("mfplat.dll")]
    static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    static extern int MFCreateAttributes(out nint attributes, int count);

    [DllImport("mf.dll")]
    static extern int MFEnumDeviceSources(nint attributes, out nint devices, out uint count);

    internal static unsafe string[] Names()
    {
        Guid type = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"),
            video = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f"),
            friendly = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        Marshal.ThrowExceptionForHR(MFStartup(0x20070, 0));
        nint attributes = 0,
            array = 0;
        uint count = 0;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 1));
            var setter = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)
                (*(nint**)attributes)[24];
            Marshal.ThrowExceptionForHR(setter(attributes, &type, &video));
            Marshal.ThrowExceptionForHR(MFEnumDeviceSources(attributes, out array, out count));
            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                nint device = Marshal.ReadIntPtr(array, i * nint.Size),
                    text = 0;
                uint length = 0;
                var get = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint*, int>)
                    (*(nint**)device)[13];
                try
                {
                    names[i] =
                        get(device, &friendly, &text, &length) >= 0
                            ? Marshal.PtrToStringUni(text, (int)length) ?? $"Camera {i}"
                            : $"Camera {i}";
                }
                finally
                {
                    Marshal.FreeCoTaskMem(text);
                }
            }
            return names;
        }
        finally
        {
            if (array != 0)
            {
                for (int i = 0; i < count; i++)
                    Marshal.Release(Marshal.ReadIntPtr(array, i * nint.Size));
                Marshal.FreeCoTaskMem(array);
            }
            if (attributes != 0)
                Marshal.Release(attributes);
            MFShutdown();
        }
    }
}

internal sealed class MotionInput : IDisposable
{
    readonly int adapter;
    CancellationTokenSource? stop;
    Task work = Task.CompletedTask;
    public long LastStamp => Interlocked.Read(ref stamp);
    long stamp;
    public Motion Latest => Volatile.Read(ref latest);
    Motion latest = Motion.Empty;
    string status = "等待输入";
    public string Status => Volatile.Read(ref status);

    public MotionInput(int adapter) => this.adapter = adapter;

    public async Task Start(Source source)
    {
        await Stop();
        var cancellation = new CancellationTokenSource();
        stop = cancellation;
        status = "正在准备 GPU 追踪…";
        latest = Motion.Empty;
        work = Task.Run(() => Run(source, cancellation.Token));
    }

    async Task Run(Source source, CancellationToken token)
    {
        try
        {
            using var tracker = new Tracking.Tracker(Program.Models, adapter);
            var hold = new OcclusionHold();
            if (token.IsCancellationRequested)
                return;
            if (source.Kind == SourceKind.Image)
            {
                using var image = Cv2.ImDecode(File.ReadAllBytes(source.Path), ImreadModes.Color);
                if (image.Empty())
                    throw new InvalidDataException("图片解码失败");
                Interlocked.Exchange(ref stamp, Stopwatch.GetTimestamp());
                Volatile.Write(ref latest, tracker.Track(image));
                status = $"图片   {image.Width} × {image.Height}";
                return;
            }
            var channel = Channel.CreateBounded<StampedFrame>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                },
                d => d.Image.Dispose()
            );
            using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var producer = Task.Run(
                () => Produce(source, channel.Writer, captureStop.Token),
                CancellationToken.None
            );
            try
            {
                await foreach (var frame in channel.Reader.ReadAllAsync(token))
                {
                    using (frame.Image)
                    {
                        var motion = hold.Update(tracker.Track(frame.Image));
                        Interlocked.Exchange(ref stamp, frame.Stamp);
                        Volatile.Write(ref latest, motion);
                    }
                }
            }
            finally
            {
                captureStop.Cancel();
                await producer;
                while (channel.Reader.TryRead(out var remaining))
                    remaining.Image.Dispose();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            status = ex.GetBaseException().Message;
            Volatile.Write(ref latest, Motion.Empty);
        }
    }

    void Produce(Source source, ChannelWriter<StampedFrame> writer, CancellationToken token)
    {
        try
        {
            using var cap = new VideoCapture();
            bool camera = source.Kind == SourceKind.Camera;
            if (camera)
            {
                if (!cap.Open(source.CameraIndex, VideoCaptureAPIs.MSMF))
                    throw new IOException(
                        "无法打开选中的摄像头。请检查设备占用及 Windows 摄像头权限。"
                    );
                cap.Set(VideoCaptureProperties.FrameWidth, 2560);
                cap.Set(VideoCaptureProperties.FrameHeight, 1440);
                cap.Set(VideoCaptureProperties.Fps, 30);
            }
            else if (!cap.Open(source.Path, VideoCaptureAPIs.FFMPEG))
                throw new IOException("视频解码失败");
            double fps = cap.Fps is > 1 and < 240 ? cap.Fps : 30;
            long frameNo = 0;
            var clock = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                var frame = new Mat();
                if (!cap.Read(frame) || frame.Empty())
                {
                    frame.Dispose();
                    if (camera)
                        throw new IOException("摄像头连接中断，可重新选择输入设备");
                    if (frameNo == 0)
                        throw new IOException("视频缺少可读取的帧");
                    cap.PosFrames = 0;
                    frameNo = 0;
                    clock.Restart();
                    continue;
                }
                status =
                    $"{(camera ? "摄像头" : "视频")}   {frame.Width} × {frame.Height}   {fps:0.#} fps";
                if (!writer.TryWrite(new StampedFrame(frame, Stopwatch.GetTimestamp())))
                    frame.Dispose();
                frameNo++;
                if (!camera)
                {
                    var delay = frameNo * 1000 / fps - clock.Elapsed.TotalMilliseconds;
                    if (delay > 1)
                        token.WaitHandle.WaitOne((int)Math.Min(1000, delay));
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    public async Task Stop()
    {
        if (stop != null)
        {
            stop.Cancel();
            await work;
            stop.Dispose();
            stop = null;
        }
        Volatile.Write(ref latest, Motion.Empty);
        status = "等待输入";
    }

    public void Dispose()
    {
        stop?.Cancel();
    }
}
