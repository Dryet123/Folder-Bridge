using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using FolderBridge.Core;
using FolderBridge.Core.Sync;
using Forms = System.Windows.Forms;

namespace FolderBridge;

sealed class Tray : IDisposable
{
    readonly App app;
    readonly Engine engine;
    readonly Forms.NotifyIcon icon;
    readonly DispatcherTimer timer;

    public Tray(App app, Engine engine)
    {
        this.app = app;
        this.engine = engine;
        icon = new Forms.NotifyIcon { Icon = CreateIcon(), Text = "Folder Bridge", Visible = true };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                app.ShowMain();
            else if (e.Button == Forms.MouseButtons.Right)
                ShowMenu();
        };
        icon.BalloonTipClicked += (_, _) => app.ShowMain();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => icon.Text = Summary();
        timer.Start();
    }

    public void Notify(string title, string text)
    {
        if (!Snapshots.Enabled)
            icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
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
        var text = $"Folder Bridge: {state}";
        return text.Length > 63 ? text[..63] : text;
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
            if (System.Windows.PresentationSource.FromVisual(menu) is HwndSource source)
                SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header, FontWeight = bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal };
        item.Click += (_, _) => action();
        return item;
    }

    static System.Drawing.Icon CreateIcon()
    {
        var size = Forms.SystemInformation.SmallIconSize.Width;
        using var bitmap = new System.Drawing.Bitmap(new MemoryStream(Icons.Png(Icons.Render(Icons.App, size))));
        var handle = bitmap.GetHicon();
        var result = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return result;
    }

    public void Dispose()
    {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
