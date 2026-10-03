namespace BatchConvertToCHD.Models;

/// <summary>Result returned by <see cref="BatchConvertToCHD.Dialogs.MessageBox" />.</summary>
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