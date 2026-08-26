using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace BatchConvertToCHD;

/// <summary>
/// About window displaying application version and information.
/// </summary>
internal partial class AboutWindow
{
    internal AboutWindow()
    {
        InitializeComponent();

        AppVersionTextBlock.Text = $"Version: {GetApplicationVersion()}";
        DescriptionTextBlock.Text =
            "A utility for batch converting various disc image formats to CHD and for verifying the integrity of CHD files.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Notify developer
            if (App.SharedBugReportService != null)
            {
                _ = App.SharedBugReportService.SendBugReportAsync($"Error opening URL: {e.Uri.AbsoluteUri}", ex);
            }

            // Notify user
            MessageBox.Show($"Unable to open link: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Mark the event as handled
        e.Handled = true;
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}