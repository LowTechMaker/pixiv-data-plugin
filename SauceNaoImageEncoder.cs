using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SceneGallery.Plugin.PixivAuthors;

/// <summary>Windows image conversion; no HTTP, response parsing or cache policy.</summary>
internal static class SauceNaoImageEncoder
{
    internal static async Task<Stream> EncodeAsync(string imagePath, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(ct).ConfigureAwait(false);
        using var input = await file.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct).ConfigureAwait(false);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
        var pixels = pixelData.DetachPixelData();
        CompositeTransparentPixelsOverWhite(pixels);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.JpegEncoderId, output).AsTask(ct).ConfigureAwait(false);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels);
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        var bytes = new byte[output.Size];
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size).AsTask(ct).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return new MemoryStream(bytes, writable: false);
    }

    internal static void CompositeTransparentPixelsOverWhite(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255) continue;
            pixels[i] = CompositeChannel(pixels[i], alpha);
            pixels[i + 1] = CompositeChannel(pixels[i + 1], alpha);
            pixels[i + 2] = CompositeChannel(pixels[i + 2], alpha);
            pixels[i + 3] = 255;
        }
    }

    private static byte CompositeChannel(byte channel, byte alpha)
    {
        var value = (channel * alpha + 255 * (255 - alpha)) / 255;
        return (byte)value;
    }
}
