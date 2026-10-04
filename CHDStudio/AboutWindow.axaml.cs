using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CHDStudio.Dialogs;
using CHDStudio.Models;
using CHDStudio.Services;

namespace CHDStudio;

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

        KeyDown += AboutWindow_KeyDown;
    }

    /// <summary>
    ///     Handles the F8 hotkey by saving a screenshot of this window.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The key event arguments.</param>
    private void AboutWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8) return;

        ScreenshotService.CaptureAndLog(this);
        e.Handled = true;
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