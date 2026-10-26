using System.Runtime.InteropServices;

namespace MdClip;

// The Windows calls the app makes that .NET does not wrap, in one place.
static class Native
{
    public const int SW_SHOWNOACTIVATE = 4;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    // A registry key's last write time (UTC FILETIME); the other outputs are not asked for.
    [DllImport("advapi32.dll")]
    public static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr cls, IntPtr clsLength, IntPtr reserved,
        IntPtr subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, IntPtr values, IntPtr maxValueNameLength, IntPtr maxValueLength,
        IntPtr securityDescriptor, out long lastWriteTime);
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
