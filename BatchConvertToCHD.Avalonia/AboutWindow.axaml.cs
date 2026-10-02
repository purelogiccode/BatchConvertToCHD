using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BatchConvertToCHD.Dialogs;

namespace BatchConvertToCHD;

/// <summary>
///     About window displaying application version and information.
/// </summary>
internal partial class AboutWindow : Window
{
    internal AboutWindow()
    {
        InitializeComponent();

        AppVersionTextBlock.Text = $"Version: {GetApplicationVersion()}";
        DescriptionTextBlock.Text =
            "A utility for batch converting various disc image formats to CHD and for verifying the integrity of CHD files.";
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OpenUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;

        try
        {
            using var process = Process.Start(
                new ProcessStartInfo { FileName = url, UseShellExecute = true }
            );
        }
        catch (Exception ex)
        {
            if (App.SharedBugReportService != null)
            {
                _ = App.SharedBugReportService.SendBugReportAsync($"Error opening URL: {url}", ex);
            }

            _ = MessageBox.ShowAsync(
                this,
                $"Unable to open link: {ex.Message}",
                "Error",
                MessageBoxButton.Ok,
                MessageBoxImage.Error
            );
        }
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}