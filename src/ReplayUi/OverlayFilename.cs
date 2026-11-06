using System.Windows;
using System.Windows.Controls;

namespace Rewind.Preview;
public partial class OverlayWindow
{
    private void OnShadowPlayChanged(object sender, RoutedEventArgs args)
    {
        if (ShadowPlayOptions is null || FilenameTokens is null) return;
        bool enabled = ShadowPlayEnabled.IsChecked == true;
        ShadowPlayOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        FilenameFormatInput.IsReadOnly = enabled;
        FilenameTokens.IsEnabled = !enabled;
        OnFilenameFormatChanged(sender, null);
    }

    private void OnFilenameFormatChanged(object sender, TextChangedEventArgs? args)
    {
        if (FilenamePreview is null) return;
        try { FilenamePreview.Text = ReplayFileName.Format(ShadowPlayEnabled?.IsChecked == true ? ReplayFileName.DefaultFormat : FilenameFormatInput.Text, "Desktop", DateTime.Now, _intervals.DefaultSeconds, 1, "1", sizeBytes: 225234125); }
        catch (ArgumentException error) { FilenamePreview.Text = error.Message; }
        RefreshSettingsPresentation();
    }
    private void OnFilenameToken(object sender, RoutedEventArgs args)
    {
        string token = (string)((Button)sender).Tag; int caret = FilenameFormatInput.SelectionStart;
        FilenameFormatInput.SelectedText = token; FilenameFormatInput.SelectionStart = caret + token.Length; FilenameFormatInput.Focus();
    }
}
