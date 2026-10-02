using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BatchConvertToCHD.Controls;

/// <summary>
///     Terminal-style, read-only text box for the activity log. Adds the small WPF
///     <c>TextBox</c> conveniences the ported code relies on (<see cref="AppendText" />,
///     <see cref="ScrollToEnd" />, <see cref="SelectionLength" /> and <see cref="SelectedText" />).
/// </summary>
internal sealed class LogTextBox : TextBox
{
    /// <summary>
    ///     Uses the base <see cref="TextBox" /> control theme so this subclass renders exactly like
    ///     a text box (Avalonia type selectors do not match derived types).
    /// </summary>
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>
    ///     Gets or sets the log text, never returning null (Avalonia's base property is nullable).
    /// </summary>
    public new string Text
    {
        get => base.Text ?? string.Empty;
        set => base.Text = value;
    }

    /// <summary>
    ///     Gets or sets the number of characters selected. Avalonia exposes the selection as
    ///     start/end indices, so this compatibility property maps onto them.
    /// </summary>
    public int SelectionLength
    {
        get => Math.Max(0, SelectionEnd - SelectionStart);
        set => SelectionEnd = SelectionStart + Math.Max(0, value);
    }

    /// <summary>
    ///     Gets or sets the selected text. Setting replaces the current selection.
    /// </summary>
    public new string SelectedText
    {
        get => base.SelectedText ?? string.Empty;
        set
        {
            var text = Text;
            var start = Math.Clamp(SelectionStart, 0, text.Length);
            var end = Math.Clamp(SelectionEnd, start, text.Length);
            Text = text.Remove(start, end - start).Insert(start, value);
            CaretIndex = start + value.Length;
        }
    }

    /// <summary>
    ///     Appends the given text to the end of the log and keeps the view scrolled to the bottom.
    /// </summary>
    /// <param name="text">The text to append.</param>
    public void AppendText(string text)
    {
        Text += text;
        CaretIndex = Text.Length;
        ScrollToEnd();
    }

    /// <summary>
    ///     Scrolls the log to the end, deferring the scroll until after the next layout pass so the
    ///     scroll extent includes the text that was just appended.
    /// </summary>
    public void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var scroller = this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroller is null) return;

                var maximum = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                scroller.Offset = new Vector(scroller.Offset.X, maximum);
            },
            DispatcherPriority.Background
        );
    }
}