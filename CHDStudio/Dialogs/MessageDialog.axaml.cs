using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CHDStudio.Models;
using CHDStudio.Services;

namespace CHDStudio.Dialogs;

/// <summary>
///     Themed modal dialog window used by <see cref="MessageBox" />.
/// </summary>
internal partial class MessageDialog : Window
{
    /// <summary>
    ///     Gets the result selected by the user. Used when the dialog is shown without an owner and
    ///     therefore without <see cref="Window.ShowDialog{TResult}" />.
    /// </summary>
    internal MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MessageDialog" /> class.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="caption">The window caption.</param>
    /// <param name="button">The buttons to offer.</param>
    /// <param name="icon">The icon to display.</param>
    internal MessageDialog(
        string message,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon
    )
    {
        InitializeComponent();

        Title = caption;
        MessageText.Text = message;

        ApplyIcon(icon);
        BuildButtons(button);

        KeyDown += MessageDialog_KeyDown;
    }

    /// <summary>
    ///     Handles the F8 hotkey by saving a screenshot of this dialog.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The key event arguments.</param>
    private void MessageDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8) return;

        ScreenshotService.CaptureAndLog(this);
        e.Handled = true;
    }

    /// <summary>
    ///     Applies the glyph, colour and visibility for the requested icon.
    /// </summary>
    /// <param name="icon">The icon requested by the caller.</param>
    private void ApplyIcon(MessageBoxImage icon)
    {
        var (glyph, color) = icon switch
        {
            MessageBoxImage.Error => ("✕", "#C42B1C"),
            MessageBoxImage.Warning => ("!", "#F9A825"),
            MessageBoxImage.Question => ("?", "#03A9F4"),
            MessageBoxImage.Information => ("i", "#03A9F4"),
            _ => (string.Empty, "#2B2B2B"),
        };

        IconCircle.Fill = Brush.Parse(color);
        IconGlyph.Text = glyph;
        IconHost.IsVisible = icon != MessageBoxImage.None;
    }

    /// <summary>
    ///     Creates the buttons for the requested button combination.
    /// </summary>
    /// <param name="button">The buttons requested by the caller.</param>
    private void BuildButtons(MessageBoxButton button)
    {
        switch (button)
        {
            case MessageBoxButton.Ok:
                ButtonsPanel.Children.Add(CreateButton("_OK", MessageBoxResult.Ok, "primary"));
                break;
            case MessageBoxButton.OkCancel:
                ButtonsPanel.Children.Add(CreateButton("_OK", MessageBoxResult.Ok, "primary"));
                ButtonsPanel.Children.Add(CreateButton("_Cancel", MessageBoxResult.Cancel, "secondary"));
                break;
            case MessageBoxButton.YesNo:
                ButtonsPanel.Children.Add(CreateButton("_Yes", MessageBoxResult.Yes, "primary"));
                ButtonsPanel.Children.Add(CreateButton("_No", MessageBoxResult.No, "secondary"));
                break;
            case MessageBoxButton.YesNoCancel:
                ButtonsPanel.Children.Add(CreateButton("_Yes", MessageBoxResult.Yes, "primary"));
                ButtonsPanel.Children.Add(CreateButton("_No", MessageBoxResult.No, "secondary"));
                ButtonsPanel.Children.Add(CreateButton("_Cancel", MessageBoxResult.Cancel, "secondary"));
                break;
            default:
                ButtonsPanel.Children.Add(CreateButton("_OK", MessageBoxResult.Ok, "primary"));
                break;
        }
    }

    /// <summary>
    ///     Creates one dialog button that closes the dialog with <paramref name="result" />.
    /// </summary>
    /// <param name="content">Button caption (with access key).</param>
    /// <param name="result">Result returned when the button is clicked.</param>
    /// <param name="styleClass">Style class applied to the button.</param>
    /// <returns>The configured button.</returns>
    private Button CreateButton(string content, MessageBoxResult result, string styleClass)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 90,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        button.Classes.Add(styleClass);
        button.Click += (_, _) =>
        {
            Result = result;
            Close(result);
        };
        ToolTip.SetTip(
            button,
            result switch
            {
                MessageBoxResult.Ok => "Confirm and close the dialog",
                MessageBoxResult.Cancel => "Dismiss the dialog without confirming",
                MessageBoxResult.Yes => "Answer yes and close the dialog",
                MessageBoxResult.No => "Answer no and close the dialog",
                _ => "Close the dialog"
            }
        );
        return button;
    }
}