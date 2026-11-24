using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FolderBridge;

public class XpWindow : Window
{
    public static readonly DependencyProperty ShowTitleIconProperty =
        DependencyProperty.Register(nameof(ShowTitleIcon), typeof(bool), typeof(XpWindow), new PropertyMetadata(false));

    public bool ShowTitleIcon
    {
        get => (bool)GetValue(ShowTitleIconProperty);
        set => SetValue(ShowTitleIconProperty, value);
    }

    public XpWindow()
    {
        SetResourceReference(StyleProperty, "XpWindow");
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        if (Snapshots.Enabled)
            ShowActivated = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int border = 0x008C1300;
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        if (Snapshots.Enabled)
        {
            Snapshots.MoveOffScreen(this);
            // Test windows sit off-screen, so they must never take keyboard focus from the desktop.
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000);
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // With WindowChrome the first SizeToContent pass still counts the standard frame; measure again.
        if (SizeToContent != SizeToContent.Manual)
        {
            var mode = SizeToContent;
            SizeToContent = SizeToContent.Manual;
            SizeToContent = mode;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
