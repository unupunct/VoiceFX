using System.Runtime.InteropServices;

namespace VoiceFX;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetLong(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetLong(IntPtr h, int i, IntPtr v);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    /// <summary>Clicks on the window never take keyboard focus away from the app you are streaming/typing in.</summary>
    public static void MakeNoActivate(IntPtr h) => SetLong(h, GWL_EXSTYLE, (IntPtr)(GetLong(h, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE));
    public static void DarkTitleBar(IntPtr h) { int v = 1; DwmSetWindowAttribute(h, 20, ref v, 4); }
}
