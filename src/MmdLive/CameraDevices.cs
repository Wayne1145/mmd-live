using System.Runtime.InteropServices;
using OpenCvSharp;

namespace MmdLive;

internal sealed record CameraMode(int Width, int Height, double Fps)
{
    // Motion smoothness depends on the sample rate more than on pixels. Among modes of at least
    // 720p, prefer 60 fps, then 30 fps, then the size closest to 1440p. The render loop runs at
    // 60 Hz, so a 60 fps camera gives one fresh observation per presented frame.
    internal static CameraMode? Prefer(IEnumerable<CameraMode> modes) =>
        modes
            .Where(m => m.Width > 0 && m.Height > 0 && m.Fps > 0)
            .OrderBy(m =>
                m.Height < 720 ? 4
                : m.Fps >= 59 ? 0
                : m.Fps >= 29 ? 1
                : m.Fps >= 20 ? 2
                : 3
            )
            .ThenBy(m =>
                Math.Abs(Math.Log(m.Width / 2560.0)) + Math.Abs(Math.Log(m.Height / 1440.0))
            )
            .ThenBy(m => Math.Abs(m.Fps - 60))
            .FirstOrDefault();
}

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

    [DllImport("mfreadwrite.dll")]
    static extern int MFCreateSourceReaderFromMediaSource(
        nint source,
        nint attributes,
        out nint reader
    );

    static Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"),
        VideoType = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f"),
        FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");

    static unsafe nint Method(nint com, int slot) => (*(nint**)com)[slot];

    static unsafe T Enumerate<T>(Func<nint[], T> visit)
    {
        Marshal.ThrowExceptionForHR(MFStartup(0x20070, 0));
        nint attributes = 0,
            array = 0;
        uint count = 0;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 1));
            Guid key = SourceType,
                value = VideoType;
            var set = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Method(attributes, 24);
            Marshal.ThrowExceptionForHR(set(attributes, &key, &value));
            Marshal.ThrowExceptionForHR(MFEnumDeviceSources(attributes, out array, out count));
            var list = new nint[count];
            for (int i = 0; i < list.Length; i++)
                list[i] = Marshal.ReadIntPtr(array, i * nint.Size);
            return visit(list);
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

    internal static unsafe string[] Names() =>
        Enumerate(list =>
            list.Select(
                    (device, index) =>
                    {
                        nint text = 0;
                        uint length = 0;
                        var key = FriendlyName;
                        var get = (delegate* unmanaged[Stdcall]<
                            nint,
                            Guid*,
                            nint*,
                            uint*,
                            int>)Method(device, 13);
                        try
                        {
                            return get(device, &key, &text, &length) >= 0
                                ? Marshal.PtrToStringUni(text, (int)length) ?? $"Camera {index}"
                                : $"Camera {index}";
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(text);
                        }
                    }
                )
                .ToArray()
        );

    // Called only for the device explicitly started in the control window. Native type inspection never requests a sample.
    internal static unsafe List<CameraMode> Modes(int index) =>
        Enumerate(devices =>
        {
            if ((uint)index >= (uint)devices.Length)
                throw new IOException("所选摄像头已移除");
            var activate = devices[index];
            nint source = 0,
                reader = 0;
            var modes = new List<CameraMode>();
            Guid mediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
            try
            {
                var open = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(
                    activate,
                    33
                );
                Marshal.ThrowExceptionForHR(open(activate, &mediaSource, &source));
                Marshal.ThrowExceptionForHR(
                    MFCreateSourceReaderFromMediaSource(source, 0, out reader)
                );
                var getType = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Method(
                    reader,
                    5
                );
                Guid sizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d"),
                    rateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
                for (uint i = 0; i < 4096; i++)
                {
                    nint type = 0;
                    if (getType(reader, 0xfffffffc, i, &type) < 0)
                        break;
                    try
                    {
                        var get = (delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Method(
                            type,
                            8
                        );
                        ulong size = 0,
                            rate = 0;
                        if (
                            get(type, &sizeKey, &size) >= 0
                            && get(type, &rateKey, &rate) >= 0
                            && (uint)rate != 0
                        )
                            modes.Add(
                                new(
                                    (int)(size >> 32),
                                    (int)(uint)size,
                                    (double)(rate >> 32) / (uint)rate
                                )
                            );
                    }
                    finally
                    {
                        Marshal.Release(type);
                    }
                }
                return modes;
            }
            finally
            {
                if (reader != 0)
                    Marshal.Release(reader);
                if (source != 0)
                {
                    var shut = (delegate* unmanaged[Stdcall]<nint, int>)Method(activate, 34);
                    shut(activate);
                    Marshal.Release(source);
                }
            }
        });

    internal static void Open(VideoCapture capture, int index)
    {
        var preferred = CameraMode.Prefer(Modes(index)) ?? new CameraMode(1920, 1080, 60);
        if (!capture.Open(index, VideoCaptureAPIs.MSMF))
            throw new IOException("无法访问摄像头。请检查 Windows 摄像头权限与设备占用。");
        capture.Set(VideoCaptureProperties.FrameWidth, preferred.Width);
        capture.Set(VideoCaptureProperties.FrameHeight, preferred.Height);
        capture.Set(VideoCaptureProperties.Fps, preferred.Fps);
    }
}
