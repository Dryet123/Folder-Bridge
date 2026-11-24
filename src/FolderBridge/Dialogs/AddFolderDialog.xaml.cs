using System.IO;
using System.Windows;
using System.Windows.Controls;
using FolderBridge.Core;
using Microsoft.Win32;

namespace FolderBridge.Dialogs;

public partial class AddFolderDialog : XpWindow
{
    readonly Engine engine;
    string autoName = "";

    public AddFolderDialog(Engine engine, IReadOnlyList<DeviceSnapshot> devices)
    {
        this.engine = engine;
        InitializeComponent();
        foreach (var d in devices)
            DeviceBoxes.Children.Add(new CheckBox { Content = d.Name, Tag = d.Id, IsChecked = true, Margin = new Thickness(0, 0, 0, 4) });
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PathBox.Focus();
    }

    public string? AddedId { get; private set; }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to sync" };
        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FolderName;
    }

    void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = Path.GetFileName(PathBox.Text.Trim().TrimEnd('\\'));
        if (NameBox.Text == autoName)
            NameBox.Text = name;
        autoName = name;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var devices = DeviceBoxes.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (DeviceId)c.Tag);
        try
        {
            AddedId = engine.AddFolder(NameBox.Text, PathBox.Text.Trim(), devices).Id;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
