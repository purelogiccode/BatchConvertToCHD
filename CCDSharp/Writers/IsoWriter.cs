using CCDSharp.Models;

namespace CCDSharp.Writers;

/// <summary>
/// Extracts user data sectors from a CloneCD .img file to produce a standard .iso file.
/// Only valid for data tracks (Mode 1 or Mode 2 Form 1).
/// </summary>
public static class IsoWriter
{
    /// <summary>
    /// Converts a CloneCD .img file to a standard .iso file by extracting 2048-byte user data sectors.
    /// </summary>
    /// <param name="imgFilePath">Path to the .img data file.</param>
    /// <param name="isoFilePath">Path for the output .iso file.</param>
    /// <param name="progress">Optional progress callback (bytesWritten, totalBytes).</param>
    /// <returns>The path to the .iso file created.</returns>
    public static string Write(
        string imgFilePath,
        string isoFilePath,
        Action<long, long>? progress = null
    )
    {
        if (!File.Exists(imgFilePath))
            throw new FileNotFoundException("IMG data file not found.", imgFilePath);

        var totalBytes = new FileInfo(imgFilePath).Length;
        var totalSectors = totalBytes / SectorConstants.RawSectorSize;

        using var input = new FileStream(
            imgFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        using var output = new FileStream(isoFilePath, FileMode.Create, FileAccess.Write);

        WriteSectors(input, output, totalSectors, progress);

        return isoFilePath;
    }

    /// <summary>
    /// Converts a CloneCD .img stream to a standard .iso stream.
    /// </summary>
    /// <param name="input">Stream containing raw 2352-byte sectors.</param>
    /// <param name="output">Stream to write 2048-byte user data sectors to.</param>
    /// <param name="totalSectors">Total number of sectors to process. If -1, reads until end of stream.</param>
    /// <param name="progress">Optional progress callback (bytesWritten, totalBytes).</param>
    public static void WriteToStream(
        Stream input,
        Stream output,
        long totalSectors = -1,
        Action<long, long>? progress = null
    )
    {
        WriteSectors(input, output, totalSectors, progress);
    }

    /// <summary>
    /// Converts a parsed DiscImage to a .iso file. Only the first data track is extracted.
    /// </summary>
    /// <param name="disc">The parsed disc image.</param>
    /// <param name="isoFilePath">Path for the output .iso file.</param>
    /// <param name="progress">Optional progress callback (bytesWritten, totalBytes).</param>
    /// <returns>The path to the .iso file created.</returns>
    public static string Write(
        DiscImage disc,
        string isoFilePath,
        Action<long, long>? progress = null
    )
    {
        if (disc.ImgFilePath == null || !File.Exists(disc.ImgFilePath))
            throw new FileNotFoundException("IMG data file not found.", disc.ImgFilePath);

        // Find the first data track
        var dataTrack = disc.Tracks.FirstOrDefault(t => !t.IsAudio);
        if (dataTrack == null)
            throw new InvalidOperationException(
                "No data track found in the disc image. ISO extraction requires a data track."
            );

        var imgLength = new FileInfo(disc.ImgFilePath).Length;
        var totalSectors = imgLength / SectorConstants.RawSectorSize;

        using var input = new FileStream(
            disc.ImgFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        using var output = new FileStream(isoFilePath, FileMode.Create, FileAccess.Write);

        WriteSectors(input, output, totalSectors, progress);

        return isoFilePath;
    }

    private static void WriteSectors(
        Stream input,
        Stream output,
        long totalSectors,
        Action<long, long>? progress
    )
    {
        var sectorBuffer = new byte[SectorConstants.RawSectorSize];
        var userDataBuffer = new byte[SectorConstants.UserDataSize];

        var totalBytesToWrite = totalSectors > 0 ? totalSectors * SectorConstants.UserDataSize : 0;
        long bytesWritten = 0;
        long sectorIndex = 0;

        while (true)
        {
            var bytesRead = ReadFully(input, sectorBuffer, SectorConstants.RawSectorSize);
            if (bytesRead == 0)
                break;

            if (bytesRead < SectorConstants.RawSectorSize)
                throw new InvalidOperationException(
                    $"Incomplete sector at index {sectorIndex}: expected {SectorConstants.RawSectorSize} bytes, got {bytesRead}."
                );

            var extracted = ExtractUserData(sectorBuffer, userDataBuffer);
            if (extracted > 0)
            {
                output.Write(userDataBuffer, 0, extracted);
                bytesWritten += extracted;
            }

            sectorIndex++;

            if (progress != null && sectorIndex % 1000 == 0)
                progress(bytesWritten, totalBytesToWrite);
        }

        // Final progress report
        progress?.Invoke(bytesWritten, bytesWritten);
    }

    /// <summary>
    /// Extracts the 2048-byte user data payload from a raw 2352-byte sector.
    /// </summary>
    /// <param name="rawSector">The raw 2352-byte sector.</param>
    /// <param name="output">Buffer to write 2048 bytes of user data to.</param>
    /// <returns>Number of bytes extracted (2048) or 0 if the sector is not a recognized data mode.</returns>
    internal static int ExtractUserData(byte[] rawSector, byte[] output)
    {
        if (rawSector.Length < SectorConstants.RawSectorSize)
            return 0;

        // Check for sync mark
        var hasSync = true;
        for (var i = 0; i < SectorConstants.SyncMark.Length; i++)
        {
            if (rawSector[i] != SectorConstants.SyncMark[i])
            {
                hasSync = false;
                break;
            }
        }

        if (!hasSync)
            return 0;

        var mode = rawSector[SectorConstants.ModeOffset];

        return mode switch
        {
            // Mode 1: user data at offset 16, length 2048
            1 => ExtractMode1(rawSector, output),
            // Mode 2: check subheader for Form 1 vs Form 2
            2 => ExtractMode2(rawSector, output),
            _ => 0,
        };
    }

    private static int ExtractMode1(byte[] rawSector, byte[] output)
    {
        // Mode 1 layout:
        // [0..11]  Sync (12 bytes)
        // [12..15] Header (4 bytes: MSF + mode)
        // [16..2063] User Data (2048 bytes)
        // [2064..2067] EDC (4 bytes)
        // [2068..2075] Zero (8 bytes)
        // [2076..2247] ECC-P (172 bytes)
        // [2248..2351] ECC-Q (104 bytes)
        Array.Copy(
            rawSector,
            SectorConstants.Mode1DataOffset,
            output,
            0,
            SectorConstants.UserDataSize
        );
        return SectorConstants.UserDataSize;
    }

    private static int ExtractMode2(byte[] rawSector, byte[] output)
    {
        // Mode 2 Form 1 layout:
        // [0..11]  Sync (12 bytes)
        // [12..15] Header (4 bytes: MSF + mode)
        // [16..23] Sub-header (8 bytes)
        // [24..2071] User Data (2048 bytes)
        // [2072..2075] EDC (4 bytes)
        // [2076..2247] ECC-P (172 bytes)
        // [2248..2351] ECC-Q (104 bytes)
        //
        // Mode 2 Form 2 layout:
        // [0..11]  Sync (12 bytes)
        // [12..15] Header (4 bytes: MSF + mode)
        // [16..23] Sub-header (8 bytes)
        // [24..2347] User Data (2324 bytes)
        // [2348..2351] EDC (4 bytes)

        // Check subheader byte at offset 18 (3rd byte of subheader) bit 5 for Form 2
        var isForm2 = (rawSector[18] & 0x20) == 0x20;

        if (isForm2)
        {
            // Form 2: 2324 bytes of user data, but we can only write 2048 for ISO
            // Extract first 2048 bytes (ISO 9660 only uses Form 1 data)
            Array.Copy(
                rawSector,
                SectorConstants.Mode2Form1DataOffset,
                output,
                0,
                SectorConstants.UserDataSize
            );
            return SectorConstants.UserDataSize;
        }

        // Form 1: 2048 bytes of user data at offset 24
        Array.Copy(
            rawSector,
            SectorConstants.Mode2Form1DataOffset,
            output,
            0,
            SectorConstants.UserDataSize
        );
        return SectorConstants.UserDataSize;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
                break;

            totalRead += read;
        }

        return totalRead;
    }
}
