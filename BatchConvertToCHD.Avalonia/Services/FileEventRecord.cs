namespace BatchConvertToCHD.Services;

internal sealed class FileEventRecord(
    DateTime timestamp,
    FileWatchEventType eventType,
    string? relatedName
)
{
    internal DateTime Timestamp { get; } = timestamp;
    internal FileWatchEventType EventType { get; } = eventType;
    internal string? RelatedName { get; } = relatedName;
}