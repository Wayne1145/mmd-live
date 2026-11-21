using System.Drawing.Imaging;
using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MmdLive.Rendering;

internal sealed unsafe partial class Renderer
{
    ID3D11Texture2D? previewMips,
        previewStaging;
    ID3D11ShaderResourceView? previewView;
    public Matrix4x4 ViewProjection { get; private set; }
    public Size ThumbnailSize => new(width / 4, height / 4);

    // Downsample the clean color buffer on the GPU before Present. Only the private control gets a copy.
    void ReadThumbnail(Bitmap bitmap)
    {
        var size = ThumbnailSize;
        if (bitmap.Size != size || bitmap.PixelFormat != PixelFormat.Format32bppPArgb)
            throw new ArgumentException("角色预览缓冲格式不匹配", nameof(bitmap));
        previewMips ??= Own(
            device.CreateTexture2D(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                mipLevels: 3,
                bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource,
                miscFlags: ResourceOptionFlags.GenerateMips
            )
        );
        previewView ??= Own(device.CreateShaderResourceView(previewMips));
        previewStaging ??= Own(
            device.CreateTexture2D(
                Format.B8G8R8A8_UNorm,
                (uint)size.Width,
                (uint)size.Height,
                bindFlags: BindFlags.None,
                usage: ResourceUsage.Staging,
                cpuAccessFlags: CpuAccessFlags.Read
            )
        );
        context.CopySubresourceRegion(previewMips, 0, 0, 0, 0, target, 0);
        context.GenerateMips(previewView);
        context.CopySubresourceRegion(previewStaging, 0, 0, 0, 0, previewMips, 2);
        var map = context.Map(previewStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bits = bitmap.LockBits(
                new Rectangle(Point.Empty, size),
                ImageLockMode.WriteOnly,
                bitmap.PixelFormat
            );
            try
            {
                int bytes = size.Width * 4;
                for (int y = 0; y < size.Height; y++)
                    Buffer.MemoryCopy(
                        (byte*)map.DataPointer + y * map.RowPitch,
                        (byte*)bits.Scan0 + y * bits.Stride,
                        bytes,
                        bytes
                    );
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
        }
        finally
        {
            context.Unmap(previewStaging, 0);
        }
    }
}
