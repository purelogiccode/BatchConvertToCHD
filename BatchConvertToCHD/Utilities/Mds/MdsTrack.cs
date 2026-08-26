namespace BatchConvertToCHD.Utilities.Mds;

/// <summary>
/// One track from an Alcohol 120% .mds track table.
/// </summary>
/// <param name="Number">Track number as recorded in the descriptor's POINT field (1-99).</param>
/// <param name="ModeByte">Raw mode byte; see <see cref="CueTrackType"/> for the meanings.</param>
/// <param name="SectorSize">Bytes per sector for this track as the rip stored them.</param>
/// <param name="StartLba">Absolute sector at which the track starts.</param>
internal sealed record MdsTrack(int Number, byte ModeByte, int SectorSize, long StartLba)
{
    // Mode values observed in real Alcohol descriptors. Form 1 and Form 2 both sit inside a 2352-byte
    // raw sector, so a cue describes either of them as MODE2/2352.
    private const byte ModeAudio = 0xA9;
    private const byte ModeMode1 = 0xAA;
    private const byte ModeMode2 = 0xEC;
    private const byte ModeMode2Form1 = 0xE2;
    private const byte ModeMode2Form2 = 0xE3;

    /// <summary>True for a CDDA audio track.</summary>
    internal bool IsAudio => ModeByte == ModeAudio;

    /// <summary>
    /// The cue TRACK type for this track once its sectors are 2352 bytes, or null when the mode is
    /// one this code has never seen and therefore cannot describe honestly.
    /// </summary>
    internal string? CueTrackType =>
        ModeByte switch
        {
            ModeAudio => "AUDIO",
            ModeMode1 => BinCueGenerator.Mode1,
            ModeMode2 or ModeMode2Form1 or ModeMode2Form2 => BinCueGenerator.Mode2,
            _ => null,
        };

    /// <summary>A short description used in log messages.</summary>
    internal string Description =>
        $"{Number}:{CueTrackType ?? $"UNKNOWN(0x{ModeByte:x2})"}@{StartLba}/ss{SectorSize}";
}