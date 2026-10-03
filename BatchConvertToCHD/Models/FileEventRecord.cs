namespace BatchConvertToCHD.Models;

/// <summary>
///     A single file-system event observed by
///     <see cref="BatchConvertToCHD.Services.FileWatcherService" />, used to explain why a file
///     disappeared between selection and processing.
/// </summary>
/// <param name="timestamp">When the event was observed.</param>
/// <param name="eventType">Kind of event.</param>
/// <param name="relatedName">Previous or new name for rename events; null otherwise.</param>
internal sealed class FileEventRecord(
    DateTime timestamp,
    FileWatchEventType eventType,
    string? relatedName
)
{
    /// <summary>When the event was observed.</summary>
    internal DateTime Timestamp { get; } = timestamp;

    /// <summary>Kind of event.</summary>
    internal FileWatchEventType EventType { get; } = eventType;

    /// <summary>Previous or new name for rename events; null otherwise.</summary>
    internal string? RelatedName { get; } = relatedName;
}