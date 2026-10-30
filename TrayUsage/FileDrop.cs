using System.Runtime.InteropServices;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace TrayUsage;

[StructLayout(LayoutKind.Sequential)]
internal struct DropPoint { public int X, Y; }

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    [PreserveSig] int DragEnter(IComDataObject data, int keyState, DropPoint point, ref int effect);
    [PreserveSig] int DragOver(int keyState, DropPoint point, ref int effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IComDataObject data, int keyState, DropPoint point, ref int effect);
}

/// <summary>
/// Lets files be dropped on the island's window (the file tray). The window is click-through unless the island is showing something,
/// so a drop lands only on a visible island: the resting pill or the panel. Anything but files is refused.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class FileDropTarget : IDropTarget
{
    private const int EffectNone = 0, EffectCopy = 1;
    private bool _accepts;

    /// <summary>Files are being dragged over the island.</summary>
    public event Action? Entered;

    /// <summary>The drag left the island without dropping.</summary>
    public event Action? Left;

    public event Action<string[]>? Dropped;

    public int DragEnter(IComDataObject data, int keyState, DropPoint point, ref int effect)
    {
        _accepts = FilesOf(data) is { Length: > 0 };
        effect = _accepts ? EffectCopy : EffectNone;
        if (_accepts) Entered?.Invoke();
        return 0;
    }

    public int DragOver(int keyState, DropPoint point, ref int effect)
    {
        effect = _accepts ? EffectCopy : EffectNone;
        return 0;
    }

    public int DragLeave()
    {
        if (_accepts) Left?.Invoke();
        _accepts = false;
        return 0;
    }

    public int Drop(IComDataObject data, int keyState, DropPoint point, ref int effect)
    {
        var files = _accepts ? FilesOf(data) : null;
        _accepts = false;
        effect = files is { Length: > 0 } ? EffectCopy : EffectNone;
        Left?.Invoke();
        if (files is { Length: > 0 }) Dropped?.Invoke(files);
        return 0;
    }

    private static string[]? FilesOf(IComDataObject data)
    {
        try
        {
            var wrapped = new DataObject(data);
            return wrapped.ContainsFileDropList() ? wrapped.GetFileDropList().Cast<string>().ToArray() : null;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or InvalidOperationException) { return null; }
    }

    public static void Register(IntPtr window, IDropTarget target)
    {
        OleInitialize(IntPtr.Zero); // S_FALSE when the thread already has it
        RegisterDragDrop(window, target);
    }

    public static void Revoke(IntPtr window) => RevokeDragDrop(window);

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr window, IDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr window);
}
