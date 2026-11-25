using System.ComponentModel;
using System.IO;
using System.Windows;

namespace FolderBridge.Dialogs;

// Shown once, the first time a downloaded copy starts. Closing it any way keeps the ticked choices.
public partial class FirstRunDialog : XpWindow
{
    readonly bool dryRun;
    bool applied;

    public FirstRunDialog(bool dryRun)
    {
        this.dryRun = dryRun;
        InitializeComponent();
        PathText.Text = $"The shortcut and autostart use {Environment.ProcessPath}. If you move the exe later, they follow it the next time it starts.";
    }

    void Ok_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (applied || dryRun)
            return;
        applied = true;
        try
        {
            DesktopShortcut.Set(ShortcutBox.IsChecked == true);
            Autostart.Set(AutostartBox.IsChecked == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, $"Couldn't finish setting up: {ex.Message}", "Folder Bridge");
        }
    }
}
