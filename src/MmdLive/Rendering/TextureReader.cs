using OpenCvSharp;
using Pfim;

namespace MmdLive.Rendering;

internal static class TextureReader
{
    public static Mat Decode(string file)
    {
        string extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".tga" or ".dds"))
            return Cv2.ImDecode(File.ReadAllBytes(file), ImreadModes.Unchanged);
        using var image = Pfimage.FromFile(file);
        if (image.Compressed)
            image.Decompress();
        image.ApplyColorMap();
        var type = image.Format switch
        {
            Pfim.ImageFormat.Rgba32 => MatType.CV_8UC4,
            Pfim.ImageFormat.Rgb24 => MatType.CV_8UC3,
            Pfim.ImageFormat.Rgb8 => MatType.CV_8UC1,
            _ => MatType.CV_8UC2,
        };
        using var view = Mat.FromPixelData(
            image.Height,
            image.Width,
            type,
            image.Data,
            image.Stride
        );
        if (type != MatType.CV_8UC2)
            return view.Clone();
        if (
            image.Format
            is not (Pfim.ImageFormat.R5g6b5 or Pfim.ImageFormat.R5g5b5 or Pfim.ImageFormat.R5g5b5a1)
        )
            throw new InvalidDataException($"贴图采用暂未支持的像素格式 {image.Format}");
        var bgra = new Mat();
        Cv2.CvtColor(
            view,
            bgra,
            image.Format == Pfim.ImageFormat.R5g6b5
                ? ColorConversionCodes.BGR5652BGRA
                : ColorConversionCodes.BGR5552BGRA
        );
        return bgra;
    }
}
