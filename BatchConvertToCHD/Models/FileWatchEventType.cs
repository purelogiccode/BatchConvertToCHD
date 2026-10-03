namespace BatchConvertToCHD.Models;

/// <summary>
///     Kind of file-system event observed by
///     <see cref="BatchConvertToCHD.Services.FileWatcherService" />.
/// </summary>
internal enum FileWatchEventType
{
    /// <summary>The file was deleted.</summary>
    Deleted,

    /// <summary>The file was renamed away from its old name.</summary>
    RenamedFrom,

    /// <summary>The file was renamed to its new name.</summary>
    RenamedTo,

    /// <summary>The file was created.</summary>
    Created
}
