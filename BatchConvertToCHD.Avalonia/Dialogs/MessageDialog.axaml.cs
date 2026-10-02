using Avalonia.Controls;
using Avalonia.Media;

namespace BatchConvertToCHD;

/// <summary>
///     Themed modal dialog window used by <see cref="MessageBox" />.
/// </summary>
internal partial class MessageDialog : Window
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="MessageDialog" /> class.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="caption">The window caption.</param>
    /// <param name="button">The buttons to offer.</param>
    /// <param name="icon">The icon to display.</param>
    public MessageDialog(
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
    }

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

    private void BuildButtons(MessageBoxButton button)
    {
        switch (button)
        {
            case MessageBoxButton.OK:
                ButtonsPanel.Children.Add(CreateButton("_OK", MessageBoxResult.OK, "primary"));
                break;
            case MessageBoxButton.OKCancel:
                ButtonsPanel.Children.Add(CreateButton("_OK", MessageBoxResult.OK, "primary"));
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
                ButtonsPanel.Children.Add(CreateButton("_OK", MessageBoxResult.OK, "primary"));
                break;
        }
    }

    private Button CreateButton(string content, MessageBoxResult result, string styleClass)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 90,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        button.Classes.Add(styleClass);
        button.Click += (_, _) => Close(result);
        return button;
    }
}
