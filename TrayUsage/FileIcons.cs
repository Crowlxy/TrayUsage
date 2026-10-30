using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TrayUsage;

/// <summary>
/// The picture Explorer shows for a file or folder (a thumbnail for an image, the program's icon otherwise), loaded off the UI thread
/// and kept by path. <see cref="Get"/> answers at once with what is there (nothing yet, or null for a path the shell has no picture of)
/// and raises <see cref="Loaded"/> when a request finishes, so the caller draws again.
/// </summary>
internal sealed class FileIcons : IDisposable
{
    private const int MinPixels = 64;
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _disposed;

    /// <summary>A picture arrived.</summary>
    public event Action? Loaded;

    /// <returns>The picture, or null while it is loading or when there is none.</returns>
    public Bitmap? Get(string path, int pixels)
    {
        if (_cache.TryGetValue(path, out var known)) return known;
        if (_disposed || !_loading.Add(path)) return null;
        var size = Math.Max(MinPixels, pixels);
        Task.Run(() => Load(path, size)).ContinueWith(task =>
        {
            void Store()
            {
                _loading.Remove(path);
                var bitmap = task.IsCompletedSuccessfully ? task.Result : null;
                if (_disposed)
                {
                    bitmap?.Dispose();
                    return;
                }
                _cache[path] = bitmap;
                Loaded?.Invoke();
            }
            if (_ui is null) Store();
            else _ui.Post(_ => Store(), null);
        });
        return null;
    }

    /// <summary>Forgets the pictures of files that left the tray.</summary>
    public void Keep(IReadOnlyCollection<string> paths)
    {
        foreach (var path in _cache.Keys.Where(key => !paths.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            _cache[path]?.Dispose();
            _cache.Remove(path);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var bitmap in _cache.Values) bitmap?.Dispose();
        _cache.Clear();
    }

    private static Bitmap? Load(string path, int pixels)
    {
        var id = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out var factory) != 0 || factory is null) return null;
        try
        {
            if (factory.GetImage(new Size(pixels, pixels), 0, out var handle) != 0 || handle == IntPtr.Zero) return null;
            try
            {
                return FromHandle(handle);
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>A copy of a 32-bit shell bitmap that keeps its transparency (<see cref="Image.FromHbitmap(IntPtr)"/> would turn it black).</summary>
    private static Bitmap? FromHandle(IntPtr handle)
    {
        if (GetObject(handle, Marshal.SizeOf<BitmapInfo>(), out var info) == 0 || info.BitsPerPixel != 32 || info.Bits == IntPtr.Zero) return null;
        // A shell bitmap is a bottom-up DIB: its pointer is the last row, so walk it with a negative stride.
        var top = IntPtr.Add(info.Bits, info.WidthBytes * (info.Height - 1));
        using var view = new Bitmap(info.Width, info.Height, -info.WidthBytes, PixelFormat.Format32bppArgb, top);
        return new Bitmap(view);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public short Planes, BitsPerPixel;
        public IntPtr Bits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr handle, int size, out BitmapInfo info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
