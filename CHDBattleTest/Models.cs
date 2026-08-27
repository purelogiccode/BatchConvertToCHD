namespace CHDBattleTest;

public enum MediaKind
{
    Cd,
    GdRom,
    Dvd,
    Hdd,
    LaserDisc,
    Unknown
}

public sealed record StepOutcome(
    string Battle,
    string Tool,
    bool Success,
    double Seconds,
    long OutputBytes,
    string? Hash,
    int ExitCode,
    double? MibPerSecond,
    double? Ratio,
    string? Error);

public sealed class FileReport
{
    public required string FileName { get; init; }
    public required string SourcePath { get; init; }
    public required long ChdBytes { get; init; }
    public ulong LogicalBytes { get; set; }
    public uint Version { get; set; }
    public MediaKind Kind { get; set; } = MediaKind.Unknown;
    public List<StepOutcome> Steps { get; } = new();
    public string? SkippedReason { get; set; }
}