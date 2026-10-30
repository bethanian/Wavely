using System.Runtime.InteropServices;

namespace Wavely.App;

internal sealed class NativeShell : IDisposable
{
    private const int HotkeyId = 0x5741;
    private readonly nint Handle;
    private readonly WindowProcedure Procedure;
    private readonly nint Previous;
    private readonly nint Icon;
    private bool TrayVisible;
    public event Action? Hotkey;
    public event Action? Restore;
    private delegate nint WindowProcedure(nint Window, uint Message, nuint WParam, nint LParam);

    public NativeShell(nint Handle)
    {
        this.Handle = Handle;
        Procedure = ProcessMessage;
        Previous = SetWindowLongPtrW(Handle, -4, Marshal.GetFunctionPointerForDelegate(Procedure));
        Icon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "Assets", "Wavely.ico"), 1, 32, 32, 0x10);
        if (Icon == 0) Icon = LoadIconW(0, 32512);
        SendMessageW(Handle, 0x80, 0, Icon);
        SendMessageW(Handle, 0x80, 1, Icon);
    }
    public bool RegisterHotkey(bool Enabled, uint Modifiers, uint Key)
    {
        UnregisterHotKey(Handle, HotkeyId);
        return !Enabled || RegisterHotKey(Handle, HotkeyId, Modifiers | 0x4000, Key);
    }
    public void SetTray(bool Visible, bool Recording)
    {
        var Data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = Handle, Id = 1, Flags = 1 | 2 | 4, CallbackMessage = 0x8001, Icon = Icon, Tip = Recording ? "Wavely — Recording · click to open" : "Wavely — click to open" };
        if (Visible) Shell_NotifyIconW(TrayVisible ? 1u : 0u, ref Data);
        else if (TrayVisible) Shell_NotifyIconW(2, ref Data);
        TrayVisible = Visible;
    }
    private nint ProcessMessage(nint Window, uint Message, nuint WParam, nint LParam)
    {
        if (Message == 0x312 && (int)WParam == HotkeyId) { Hotkey?.Invoke(); return 0; }
        if (Message == 0x8001 && ((int)LParam == 0x202 || (int)LParam == 0x205)) { Restore?.Invoke(); return 0; }
        return CallWindowProcW(Previous, Window, Message, WParam, LParam);
    }
    public static void ShowExisting()
    {
        var Window = FindWindowW(null, "Wavely");
        if (Window != 0) { ShowWindow(Window, 9); SetForegroundWindow(Window); }
    }
    public void Show() { ShowWindow(Handle, 9); SetForegroundWindow(Handle); }
    public void Dispose()
    {
        SetTray(false, false);
        UnregisterHotKey(Handle, HotkeyId);
        SetWindowLongPtrW(Handle, -4, Previous);
        if (Icon != 0) DestroyIcon(Icon);
        GC.KeepAlive(Procedure);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint Window, int Index, nint Value);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint Previous, nint Window, uint Message, nuint WParam, nint LParam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint Window, int Id, uint Modifiers, uint Key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint Window, int Id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint Message, ref NotifyIconData Data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint Instance, string Name, uint Type, int Width, int Height, uint Flags);
    [DllImport("user32.dll")] private static extern nint LoadIconW(nint Instance, int Name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint Icon);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint Window, uint Message, nuint WParam, nint LParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? ClassName, string Title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint Window, int Command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint Window);
}
