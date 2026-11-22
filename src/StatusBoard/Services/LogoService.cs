using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StatusBoard.Core;

namespace StatusBoard.Services;

/// <summary>
/// Vendor logos, cached in a "logos" folder next to the exe (portable). File names include a hash of the vendor's Logo setting, so
/// changing it fetches a fresh one; a Logo that is a file path is loaded directly. Delete the folder to re-download everything.
/// </summary>
public sealed class LogoService(IHttpFetcher http)
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "logos");
    private readonly HashSet<string> _failedThisRun = [];
    private readonly Dictionary<string, Task<ImageSource?>> _inFlight = [];

    /// <summary>Overlapping requests for one vendor share a download. Call on the UI thread.</summary>
    public Task<ImageSource?> GetAsync(VendorConfig vendor, CancellationToken ct)
    {
        var key = $"{vendor.Id}|{vendor.Logo}|{vendor.StatusPageUrl}";
        if (_inFlight.TryGetValue(key, out var running)) return running;
        var task = LoadAsync(vendor, ct);
        if (task.IsCompleted) return task;
        _inFlight[key] = task;
        return Finish();
        async Task<ImageSource?> Finish() { try { return await task; } finally { _inFlight.Remove(key); } }
    }

    private async Task<ImageSource?> LoadAsync(VendorConfig vendor, CancellationToken ct)
    {
        try
        {
            var setting = vendor.Logo?.Trim();
            if (!string.IsNullOrEmpty(setting) && !(Uri.TryCreate(setting, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            {
                var path = Path.IsPathRooted(setting) ? setting : Path.Combine(AppContext.BaseDirectory, setting); // a local file
                return File.Exists(path) ? Decode(await File.ReadAllBytesAsync(path, ct)) : null;
            }

            var key = $"{Safe(vendor.Id)}-{Hash(vendor.Logo + "|" + vendor.StatusPageUrl)}";
            if (Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, key + ".*").FirstOrDefault() is { } cached) return Decode(await File.ReadAllBytesAsync(cached, ct));
            if (!_failedThisRun.Add(key)) return null; // don't hammer a vendor whose logo can't be found

            if (await LogoFinder.FindAsync(vendor, http, ct) is not { } logo || Decode(logo.Bytes) is not { } image) return null;
            _failedThisRun.Remove(key);
            try
            {
                Directory.CreateDirectory(_dir);
                foreach (var old in Directory.EnumerateFiles(_dir, Safe(vendor.Id) + "-*.*")) File.Delete(old); // stale after a Logo change
                File.WriteAllBytes(Path.Combine(_dir, key + logo.Extension), logo.Bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // read-only folder: memory only
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException) { return null; }
    }

    /// <summary>Removes cached logos for vendors that no longer exist.</summary>
    public void Prune(IEnumerable<string> vendorIds)
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var keep = vendorIds.Select(Safe).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(_dir))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.LastIndexOf('-') is > 0 and var dash && !keep.Contains(name[..dash])) File.Delete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Largest size a logo is kept at in memory: sharp on the biggest tile (TV mode, 2.5× zoom, 200% scaling).</summary>
    public const int MaxPixels = 256;
    /// <summary>Anything claiming to be bigger than this is refused before its pixels are decoded (a tiny file can claim to be huge).</summary>
    private const int MaxSourcePixels = 8192;

    /// <summary>PNG/ICO/JPEG/GIF, largest frame of an .ico, scaled down to <see cref="MaxPixels"/>; null if unusable.</summary>
    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            // Read the headers only (no pixels yet) to pick a frame and check its size.
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames
                .OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
            if (frame is null || frame.PixelWidth < 8 || frame.PixelWidth > MaxSourcePixels || frame.PixelHeight > MaxSourcePixels) return null;
            BitmapSource source = frame;
            var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longest > MaxPixels) source = new TransformedBitmap(frame, new ScaleTransform((double)MaxPixels / longest, (double)MaxPixels / longest));
            var image = new CachedBitmap(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); // decoded now, at the small size
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or COMException) { return null; }
    }

    private static string Safe(string id) => new(id.Select(c => char.IsLetterOrDigit(c) || c is '_' ? c : '_').ToArray());
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
}
