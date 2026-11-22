using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StatusBoard.Services;

public sealed class LogoImportException(string message) : Exception(message);

public sealed record ImportedLogo(string RelativePath, BitmapSource Image, string? Warning);

/// <summary>
/// Turns any image the user picks into a tile logo that matches the built-in ones: empty or white margins are trimmed, the mark is
/// centred on a white square with even padding, and the result is saved as a PNG in "logos\custom" next to the exe (portable;
/// the vendor's Logo setting points at it). Unused files there are removed by <see cref="PruneUnused"/>.
/// </summary>
public static class LogoImporter
{
    public const int Size = 256;
    /// <summary>The mark fills this share of the square; the rest is white margin.</summary>
    public const double Fill = 0.80;
    private const long MaxBytes = 20 * 1024 * 1024;

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "logos", "custom");

    public static bool IsCustom(string? logo) =>
        logo?.Replace('\\', '/').TrimStart('/').StartsWith("logos/custom/", StringComparison.OrdinalIgnoreCase) == true;

    public static ImportedLogo Import(string file, string vendorId)
    {
        byte[] bytes;
        try
        {
            if (new FileInfo(file).Length > MaxBytes) throw new LogoImportException("That file is over 20 MB. Choose a smaller image.");
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LogoImportException($"Couldn't read that file: {ex.Message}"); }

        var source = Decode(bytes) ?? throw new LogoImportException(
            Path.GetExtension(file).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? "SVG files aren't supported. Export the logo as a PNG and try again."
                : "That file isn't an image Status Board can read. Use a PNG, JPG, BMP, GIF, ICO or TIFF.");

        var (png, warning) = Compose(source);
        var name = $"{Safe(vendorId)}-{Convert.ToHexString(SHA256.HashData(png))[..8].ToLowerInvariant()}.png";
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(Path.Combine(Dir, name), png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LogoImportException($"Couldn't save the logo next to the app ({ex.Message}). Is the folder read-only?");
        }
        return new ImportedLogo($"logos/custom/{name}", Decode(png)!, warning);
    }

    /// <summary>Trim, centre on white, encode as PNG. Also returns a warning when the result may not look right.</summary>
    public static (byte[] Png, string? Warning) Compose(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);

        // Bounds of the mark: pixels that are neither (nearly) transparent nor (nearly) white.
        int minX = w, minY = h, maxX = -1, maxY = -1;
        var opaque = false;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * stride + x * 4;
                if (px[i + 3] <= 24) continue;
                opaque = true;
                if (px[i] > 238 && px[i + 1] > 238 && px[i + 2] > 238) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

        string? warning = null;
        if (maxX < 0)
        {
            (minX, minY, maxX, maxY) = (0, 0, w - 1, h - 1);
            warning = opaque ? "This logo is white, so it won't show on the white tile. Try a coloured or dark version." : "This image looks empty.";
        }
        var crop = new CroppedBitmap(bgra, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        if (warning is null && Math.Max(crop.PixelWidth, crop.PixelHeight) < 48)
            warning = $"This image is small ({crop.PixelWidth}×{crop.PixelHeight}) and may look blurry. A larger one (200 px or more) works best.";

        var scale = Size * Fill / Math.Max(crop.PixelWidth, crop.PixelHeight);
        double dw = crop.PixelWidth * scale, dh = crop.PixelHeight * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Size, Size));
            dc.DrawImage(crop, new Rect((Size - dw) / 2, (Size - dh) / 2, dw, dh));
        }
        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return (ms.ToArray(), warning);
    }

    /// <summary>Deletes custom logos no vendor uses any more (e.g. replaced, or picked and then cancelled).</summary>
    public static void PruneUnused(IEnumerable<string?> logoSettings)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var used = logoSettings.Where(IsCustom).Select(l => Path.GetFileName(l!.Replace('\\', '/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(Dir))
                if (!used.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Large images are decoded straight to this size: a 10,000 px photo would otherwise need ~400 MB.</summary>
    public const int MaxSide = 1024;

    /// <summary>Largest frame (for .ico), at most <see cref="MaxSide"/> on its longer side, or null if unreadable.</summary>
    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            // Read sizes without decoding pixels yet.
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
            if (frame is null || frame.PixelWidth < 1 || frame.PixelHeight < 1) return null;
            int w = frame.PixelWidth, h = frame.PixelHeight;

            BitmapSource result;
            if (Math.Max(w, h) <= MaxSide) result = new CachedBitmap(frame, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            else if (decoder.Frames.Count == 1)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(bytes);
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (w >= h) image.DecodePixelWidth = MaxSide; else image.DecodePixelHeight = MaxSide;
                image.EndInit();
                result = image;
            }
            else
            {
                var scale = (double)MaxSide / Math.Max(w, h);
                result = new CachedBitmap(new TransformedBitmap(frame, new ScaleTransform(scale, scale)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or COMException
                                      or OverflowException or OutOfMemoryException or IOException) { return null; }
    }

    private static string Safe(string id) => new(id.Select(c => char.IsLetterOrDigit(c) || c is '_' ? c : '_').ToArray());
}
