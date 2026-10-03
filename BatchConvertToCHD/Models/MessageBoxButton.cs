namespace BatchConvertToCHD.Models;

/// <summary>Buttons shown by <see cref="BatchConvertToCHD.Dialogs.MessageBox" />.</summary>
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