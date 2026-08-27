namespace CCDSharp.Parsers;

/// <summary>
/// Provides access to subchannel data from CloneCD .sub files.
/// Each sector has 96 bytes of subchannel data (P, Q, R, S, T, U, V, W channels).
/// </summary>
public sealed class SubcodeParser : IDisposable
{
    private readonly bool _ownsStream;

    /// <summary>
    /// Size of subchannel data per sector (96 bytes).
    /// </summary>
    public const int SubchannelSize = 96;

    /// <summary>
    /// Initializes a new SubcodeParser from a file path.
    /// </summary>
    /// <param name="subFilePath">Path to the .sub file.</param>
    public SubcodeParser(string subFilePath)
    {
        if (!File.Exists(subFilePath))
            throw new FileNotFoundException("SUB file not found.", subFilePath);

        BaseStream = new FileStream(subFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ownsStream = true;
    }

    /// <summary>
    /// Initializes a new SubcodeParser from an existing stream.
    /// </summary>
    /// <param name="stream">The stream containing subchannel data.</param>
    /// <param name="ownsStream">Whether the parser owns the stream (disposes it when done).</param>
    public SubcodeParser(Stream stream, bool ownsStream = false)
    {
        BaseStream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
    }

    /// <summary>
    /// Gets the total number of sectors in the subchannel file.
    /// </summary>
    public long SectorCount => BaseStream.Length / SubchannelSize;

    /// <summary>
    /// Reads subchannel data for a specific sector.
    /// </summary>
    /// <param name="sectorIndex">The zero-based sector index.</param>
    /// <returns>A 96-byte array of subchannel data, or null if the sector is out of range.</returns>
    public byte[]? ReadSubchannel(long sectorIndex)
    {
        var offset = sectorIndex * SubchannelSize;
        if (offset + SubchannelSize > BaseStream.Length)
            return null;

        var data = new byte[SubchannelSize];
        BaseStream.Seek(offset, SeekOrigin.Begin);
        var read = BaseStream.Read(data, 0, SubchannelSize);
        return read == SubchannelSize ? data : null;
    }

    /// <summary>
    /// Reads subchannel data for a range of sectors.
    /// </summary>
    /// <param name="startSector">The zero-based start sector index.</param>
    /// <param name="count">Number of sectors to read.</param>
    /// <returns>A list of 96-byte arrays. May be shorter than count if end of file is reached.</returns>
    public IList<byte[]> ReadSubchannels(long startSector, int count)
    {
        var result = new List<byte[]>(count);
        var offset = startSector * SubchannelSize;

        if (offset >= BaseStream.Length)
            return result;

        BaseStream.Seek(offset, SeekOrigin.Begin);

        for (var i = 0; i < count; i++)
        {
            var data = new byte[SubchannelSize];
            var read = BaseStream.Read(data, 0, SubchannelSize);
            if (read < SubchannelSize)
                break;

            result.Add(data);
        }

        return result;
    }

    /// <summary>
    /// Gets the raw stream for sequential reading.
    /// </summary>
    private Stream BaseStream { get; }

    public void Dispose()
    {
        if (_ownsStream)
            BaseStream.Dispose();
    }
}