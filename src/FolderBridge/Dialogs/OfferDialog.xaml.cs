using System.IO;
using System.Windows;
using FolderBridge.Core;
using Microsoft.Win32;

namespace FolderBridge.Dialogs;

public partial class OfferDialog : XpWindow
{
    readonly Engine engine;
    readonly FolderOffer offer;

    public OfferDialog(Engine engine, FolderOffer offer)
    {
        this.engine = engine;
        this.offer = offer;
        InitializeComponent();
        Intro.Text = $"{offer.DeviceName} wants to share the folder \"{offer.FolderName}\" with this computer.";
        var safe = string.Concat(offer.FolderName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        PathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Folder Bridge", safe.Length > 0 ? safe : "Shared");
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = $"Where to keep \"{offer.FolderName}\"" };
        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FolderName;
    }

    void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            engine.AcceptOffer(offer, PathBox.Text.Trim());
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    void Ignore_Click(object sender, RoutedEventArgs e)
    {
        engine.IgnoreOffer(offer);
        Close();
    }
}
