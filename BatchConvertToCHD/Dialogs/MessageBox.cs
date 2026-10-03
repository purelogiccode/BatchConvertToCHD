using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Dialogs;

/// <summary>
///     Themed modal message box rendering a <see cref="MessageDialog" />. Keeps the call
///     sites readable while running on Windows, Linux and macOS.
/// </summary>
internal static class MessageBox
{
    /// <summary>Shows a modal message box without an explicit owner.</summary>
    internal static Task<MessageBoxResult> ShowAsync(
        string messageBoxText,
        string caption,
        MessageBoxButton button = MessageBoxButton.Ok,
        MessageBoxImage icon = MessageBoxImage.None
    )
    {
        return ShowAsync(null, messageBoxText, caption, button, icon);
    }

    /// <summary>Shows a modal message box owned by <paramref name="owner" /> (or the main window).</summary>
    internal static async Task<MessageBoxResult> ShowAsync(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon
    )
    {
        var dialog = new MessageDialog(messageBoxText, caption, button, icon);
        var dialogOwner = owner ?? GetMainWindow();
        if (dialogOwner is null)
        {
            var completion = new TaskCompletionSource<MessageBoxResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            dialog.Closed += (_, _) => completion.TrySetResult(dialog.Result);
            dialog.Show();
            return await completion.Task.ConfigureAwait(true);
        }

        return await dialog.ShowDialog<MessageBoxResult>(dialogOwner).ConfigureAwait(true);
    }

    /// <summary>
    ///     Gets the application's main window, or null when no desktop lifetime is active.
    /// </summary>
    /// <returns>The main window used as the default dialog owner.</returns>
    private static Window? GetMainWindow()
    {
        return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
    }
}