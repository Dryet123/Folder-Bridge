using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using FolderBridge.Core;
using FolderBridge.Core.Sync;

namespace FolderBridge;

// Tray icon through Shell_NotifyIcon directly, so WinForms never has to be loaded.
sealed class Tray : IDisposable
{
    const int CallbackMessage = 0x8001;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, NIN_BALLOONUSERCLICK = 0x405;

    readonly App app;
    readonly Engine engine;
    readonly HwndSource window;
    readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly IntPtr icon;
    DateTime tipUpdated;

    public Tray(App app, Engine engine)
    {
        this.app = app;
        this.engine = engine;
        window = new HwndSource(new HwndSourceParameters("Folder Bridge tray") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        window.AddHook(WndProc);
        icon = CreateIcon();
        Update(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP, "Folder Bridge");
    }

    public void Notify(string title, string text)
    {
        if (Snapshots.Enabled)
            return;
        var data = Data(NIF_INFO, null);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = 1;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == taskbarCreated)
        {
            // Explorer restarted and forgot our icon.
            Update(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP, Summary());
            return IntPtr.Zero;
        }
        if (msg != CallbackMessage)
            return IntPtr.Zero;
        switch ((int)lParam & 0xFFFF)
        {
            case WM_LBUTTONUP:
            case NIN_BALLOONUSERCLICK:
                app.ShowMain();
                break;
            case WM_RBUTTONUP:
                ShowMenu();
                break;
            case WM_MOUSEMOVE when DateTime.UtcNow - tipUpdated > TimeSpan.FromSeconds(2):
                // The tooltip is only worth refreshing while someone is pointing at the icon.
                tipUpdated = DateTime.UtcNow;
                Update(NIM_MODIFY, NIF_TIP, Summary());
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    string Summary()
    {
        var s = engine.Snapshot();
        string state = s.Folders switch
        {
            [] => "No folders yet",
            _ when s.Folders.Any(f => f.State == FolderState.Error) => "A folder needs attention",
            _ when s.Folders.Any(f => f.State is FolderState.Syncing or FolderState.Scanning || f.Sending) => "Syncing...",
            _ when s.Folders.All(f => f.Paused) => "Paused",
            _ when s.Devices.All(d => !d.Connected) => "Waiting for other computers",
            _ => "Up to date",
        };
        return $"Folder Bridge: {state}";
    }

    void ShowMenu()
    {
        var allPaused = engine.Snapshot().Folders is { Count: > 0 } folders && folders.All(f => f.Paused);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Open Folder Bridge", app.ShowMain, true));
        menu.Items.Add(Item("Sync all now", () => engine.Rescan()));
        menu.Items.Add(Item(allPaused ? "Resume all" : "Pause all", () => engine.SetAllPaused(!allPaused)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", app.Quit));
        menu.Opened += (_, _) =>
        {
            // Without focus the menu would stay open after clicking elsewhere.
            if (PresentationSource.FromVisual(menu) is HwndSource source)
                SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
        item.Click += (_, _) => action();
        return item;
    }

    static IntPtr CreateIcon()
    {
        int size = GetSystemMetrics(49);
        var png = Icons.Png(Icons.Render(Icons.App, size));
        return CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
    }

    NOTIFYICONDATA Data(uint flags, string? tip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tip ?? "",
        szInfo = "",
        szInfoTitle = "",
    };

    void Update(uint message, uint flags, string tip)
    {
        var data = Data(flags, tip.Length > 127 ? tip[..127] : tip);
        Shell_NotifyIcon(message, ref data);
    }

    public void Dispose()
    {
        var data = Data(0, null);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        DestroyIcon(icon);
        window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);
}
