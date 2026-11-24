using System.Windows;

namespace FolderBridge.Dialogs;

public partial class MessageDialog : XpWindow
{
    MessageDialog(string title, string text, string ok)
    {
        InitializeComponent();
        Title = title;
        Message.Text = text;
        OkButton.Content = ok;
    }

    public static bool Confirm(Window owner, string title, string text, string ok) =>
        new MessageDialog(title, text, ok) { Owner = owner }.ShowDialog() == true;

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
