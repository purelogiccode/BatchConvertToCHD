namespace BatchConvertToCHD.Services;

internal enum FileWatchEventType
{
    Deleted,
    RenamedFrom,
    RenamedTo,
    Created
}