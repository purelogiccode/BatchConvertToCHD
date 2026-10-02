using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace BatchConvertToCHD.Dialogs;

/// <summary>Buttons shown by <see cref="MessageBox" />.</summary>
internal enum MessageBoxButton
{
    /// <summary>Only an OK button.</summary>
    Ok,

    /// <summary>OK and Cancel buttons.</summary>
    OkCancel,

    /// <summary>Yes and No buttons.</summary>
    YesNo,

    /// <summary>Yes, No and Cancel buttons.</summary>
    YesNoCancel
}

/// <summary>Icon shown by <see cref="MessageBox" />.</summary>
internal enum MessageBoxImage
{
    /// <summary>No icon.</summary>
    None,

    /// <summary>Error icon.</summary>
    Error,

    /// <summary>Question icon.</summary>
    Question,

    /// <summary>Warning icon.</summary>
    Warning,

    /// <summary>Information icon.</summary>
    Information
}

/// <summary>Result returned by <see cref="MessageBox" />.</summary>
internal enum MessageBoxResult
{
    /// <summary>No result (dialog closed without a choice).</summary>
    None,

    /// <summary>The OK button was chosen.</summary>
    Ok,

    /// <summary>The Cancel button was chosen.</summary>
    Cancel,

    /// <summary>The Yes button was chosen.</summary>
    Yes,

    /// <summary>The No button was chosen.</summary>
    No
}

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
            dialog.Show();
            return MessageBoxResult.None;
        }

        return await dialog.ShowDialog<MessageBoxResult>(dialogOwner).ConfigureAwait(true);
    }

    private static Window? GetMainWindow()
    {
        return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
    }
}