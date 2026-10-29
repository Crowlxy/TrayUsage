using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TrayUsage;

internal sealed class CapsLockHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int VkCapital = 0x14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private readonly HookProc _callback;
    private IntPtr _handle;
    private bool _isDown;

    public event Action? Pressed;
    public event Action? Released;

    public CapsLockHook()
    {
        // A previous Caps Lock state would remain active after the key is intercepted.
        if ((GetKeyState(VkCapital) & 1) != 0)
        {
            KeybdEvent(VkCapital, 0, 0, UIntPtr.Zero);
            KeybdEvent(VkCapital, 0, 0x0002, UIntPtr.Zero);
        }

        _callback = OnKeyboardEvent;
        _handle = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr OnKeyboardEvent(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(_handle, code, message, data);
        var key = Marshal.PtrToStructure<KbdLlHookStruct>(data);
        if (key.VkCode != VkCapital) return CallNextHookEx(_handle, code, message, data);

        var kind = message.ToInt32();
        if (kind is WmKeyDown or WmSysKeyDown)
        {
            if (!_isDown) { _isDown = true; Pressed?.Invoke(); }
            return (IntPtr)1;
        }
        if (kind is WmKeyUp or WmSysKeyUp)
        {
            if (_isDown) { _isDown = false; Released?.Invoke(); }
            return (IntPtr)1;
        }
        return CallNextHookEx(_handle, code, message, data);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte key, byte scan, uint flags, UIntPtr extraInfo);
}
