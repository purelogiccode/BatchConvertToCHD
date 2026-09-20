namespace ISZSharp;

/// <summary>
///     CRC-32 (IEEE 802.3, the one zlib calls crc32) used to validate the checksum UltraISO writes at
///     the end of its 64-byte header.
///     The header stores the checksum as <see cref="Finish" /> returns it: the CRC register with the
///     usual final complement left off, which is what isz-tool compares against on real files.
/// </summary>
internal sealed class IszCrc32
{
    /// <summary>Lookup table for the reflected 0xEDB88320 polynomial, built once.</summary>
    private static readonly uint[] Table = BuildTable();

    /// <summary>
    ///     The final value as the ISZ header stores it: the standard CRC-32 result with the usual
    ///     final complement not applied (equivalently, the complement of the standard value).
    /// </summary>
    internal uint Finish { get; private set; } = 0xFFFFFFFF;

    /// <summary>Adds a run of bytes to the checksum.</summary>
    /// <param name="data">Bytes to add.</param>
    internal void Append(ReadOnlySpan<byte> data)
    {
        var crc = Finish;

        foreach (var value in data) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

        Finish = crc;
    }

    /// <summary>Builds the reflected CRC-32 lookup table.</summary>
    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;

            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;

            table[index] = value;
        }

        return table;
    }
}