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

internal sealed class MotionInput : IDisposable
{
    readonly int adapter;
    CancellationTokenSource? stop;
    Task work = Task.CompletedTask;
    readonly PreviewMailbox frames = new();
    public volatile bool PreviewVisible;
    public Motion Latest => frames.Latest.Driven;

    public InputSample Read(out Bitmap? preview)
    {
        var sample = frames.Read(out preview);
        if (stop?.IsCancellationRequested != true)
            return sample;
        preview?.Dispose();
        preview = null;
        return InputSample.Empty;
    }

    string status = "等待输入";
    public string Status => Volatile.Read(ref status);

    public MotionInput(int adapter) => this.adapter = adapter;

    public async Task Start(Source source)
    {
        await Stop();
        var cancellation = new CancellationTokenSource();
        stop = cancellation;
        status = "正在准备 GPU 追踪…";
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
                long stamp = Stopwatch.GetTimestamp();
                var detected = tracker.Track(image);
                token.ThrowIfCancellationRequested();
                frames.Publish(new(detected, detected, stamp), PreviewMailbox.Downsample(image));
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
                        var detected = tracker.Track(frame.Image);
                        var driven = hold.Update(detected);
                        token.ThrowIfCancellationRequested();
                        frames.Publish(
                            new(detected, driven, frame.Stamp),
                            PreviewVisible ? PreviewMailbox.Downsample(frame.Image) : null
                        );
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
            frames.Publish(InputSample.Empty, null);
        }
    }

    void Produce(Source source, ChannelWriter<StampedFrame> writer, CancellationToken token)
    {
        try
        {
            using var cap = new VideoCapture();
            bool camera = source.Kind == SourceKind.Camera;
            if (camera)
                CameraDevices.Open(cap, source.CameraIndex);
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
        frames.Publish(InputSample.Empty, null);
        status = "等待输入";
    }

    public void Dispose()
    {
        stop?.Cancel();
        frames.Dispose();
    }
}
