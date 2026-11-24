using System.Windows;
using FolderBridge.Core;

namespace FolderBridge.Dialogs;

public partial class SettingsDialog : XpWindow
{
    readonly Engine engine;

    public SettingsDialog(Engine engine)
    {
        this.engine = engine;
        InitializeComponent();
        var s = engine.Snapshot();
        NameBox.Text = s.DeviceName;
        PortBox.Text = s.Port.ToString();
        IdBox.Text = s.DeviceId.Grouped;
        AutostartBox.IsChecked = Autostart.IsEnabled;
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(IdBox.Text);

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var port))
                throw new ArgumentException("The port must be a number.");
            engine.UpdateSettings(NameBox.Text, port);
            if (AutostartBox.IsChecked != Autostart.IsEnabled)
                Autostart.Set(AutostartBox.IsChecked == true);
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
