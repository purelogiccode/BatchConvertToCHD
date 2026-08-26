using System.Buffers.Binary;
using System.Globalization;
using System.IO;

namespace BatchConvertToCHD.Utilities.Ecm;

/// <summary>
/// Decodes ECM (Error Code Modeler) files back to the disc image they were made from.
///
/// ECM shrinks a raw CD image by discarding the per-sector EDC checksum and Reed-Solomon parity,
/// which are fully derivable from the user data, and recording only what kind of sector each one
/// was. The file is a sequence of blocks, each introduced by a variable-length number whose low two
/// bits give the kind: literal bytes, Mode 1, Mode 2 Form 1 or Mode 2 Form 2. A four-byte checksum
/// of the whole restored image closes the file, so a damaged ECM is detectable rather than silently
/// producing a plausible image.
///
/// Decoding happens in-process. An earlier version drove Neill Corlett's external UNECM tool
/// instead, because regenerating the parity cannot be trusted without a known-good fixture to check
/// against; that fixture now exists, and the output is verified byte for byte against the original
/// tool, so the external dependency is gone and ARM64 is covered like every other format.
/// </summary>
internal static class EcmImageDecoder
{
    /// <summary>The four bytes every ECM file opens with: "ECM" and a zero.</summary>
    internal static readonly byte[] Signature = "ECM\0"u8.ToArray();

    /// <summary>Stream buffer size. Disc-sized files, so keep it large.</summary>
    private const int BufferBytes = 1024 * 1024;

    /// <summary>How often decoding progress is logged, as a fraction of the input.</summary>
    private const int ProgressStepPercent = 10;

    /// <summary>Block kinds, taken from the low two bits of a block's leading number.</summary>
    private const int TypeLiteral = 0;

    private const int TypeMode1 = 1;
    private const int TypeMode2Form1 = 2;
    private const int TypeMode2Form2 = 3;

    /// <summary>Ends the block list. Stored as the un-incremented count, so it is checked before the increment.</summary>
    private const uint EndOfBlocks = 0xFFFFFFFF;

    /// <summary>A count this large cannot be honest and would overflow the arithmetic below.</summary>
    private const uint ImplausibleCount = 0x80000000;

    /// <summary>The name the decoded image should be given: the .ecm suffix simply comes off.</summary>
    /// <param name="ecmPath">Path of the .ecm file.</param>
    internal static string GetDecodedFileName(string ecmPath)
    {
        var name = Path.GetFileName(ecmPath);

        return name.EndsWith(FileExtensions.Ecm, StringComparison.OrdinalIgnoreCase)
            ? name[..^FileExtensions.Ecm.Length]
            : Path.GetFileNameWithoutExtension(name) + FileExtensions.Bin;
    }

    /// <summary>
    /// Decodes <paramref name="ecmPath"/> to <paramref name="destinationPath"/>.
    /// </summary>
    /// <param name="ecmPath">Path of the .ecm file.</param>
    /// <param name="destinationPath">File to write the restored image to.</param>
    /// <param name="onLog">Log callback.</param>
    /// <param name="token">Cancellation token.</param>
    internal static Task<EcmDecodeResult> DecodeAsync(string ecmPath, string destinationPath, Action<string> onLog,
        CancellationToken token)
    {
        // The format is a byte-at-a-time state machine over the whole image, so it runs as one
        // blocking job off the UI thread rather than awaiting per read.
        return Task.Run(() => Decode(ecmPath, destinationPath, onLog, token), token);
    }

    private static EcmDecodeResult Decode(string ecmPath, string destinationPath, Action<string> onLog,
        CancellationToken token)
    {
        try
        {
            using var input = new FileStream(ecmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferBytes);
            using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferBytes);

            var header = new byte[Signature.Length];
            if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length ||
                !header.AsSpan().SequenceEqual(Signature))
            {
                return EcmDecodeResult.Failed("the file does not start with an ECM header, so it is not an ECM file.");
            }

            var sector = new byte[CdSectorEccEdc.SectorSize];
            var inputLength = input.Length;
            var nextProgressPercent = ProgressStepPercent;
            uint runningEdc = 0;
            long written = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (!TryReadBlockHeader(input, out var type, out var count))
                {
                    return TruncatedFailure();
                }

                if (count == EndOfBlocks)
                {
                    break;
                }

                if (count >= ImplausibleCount - 1)
                {
                    return CorruptFailure();
                }

                count++;

                var ok = type switch
                {
                    TypeLiteral => TryCopyLiteral(input, output, sector, count, ref runningEdc, ref written),
                    TypeMode1 => TryExpandMode1(input, output, sector, count, ref runningEdc, ref written),

                    // The block header masks its kind to two bits, so these four are the only values
                    // possible; both Mode 2 forms expand through the same routine.
                    TypeMode2Form1 or TypeMode2Form2 => TryExpandMode2(input, output, sector, count,
                        type == TypeMode2Form1, ref runningEdc, ref written),
                    _ => false
                };

                if (!ok)
                {
                    return TruncatedFailure();
                }

                if (inputLength > 0)
                {
                    var percent = (int)(input.Position * 100 / inputLength);
                    if (percent >= nextProgressPercent)
                    {
                        onLog($" Decoded {percent.ToString(CultureInfo.InvariantCulture)}% of the ECM file.");
                        nextProgressPercent = percent - percent % ProgressStepPercent + ProgressStepPercent;
                    }
                }
            }

            // The file ends with the EDC of the whole restored image. This is the only check that the
            // regenerated parity and the recovered data are actually right, so it is not optional.
            var trailer = new byte[4];
            if (input.ReadAtLeast(trailer, trailer.Length, throwOnEndOfStream: false) < trailer.Length)
            {
                return TruncatedFailure();
            }

            var expectedEdc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
            if (expectedEdc != runningEdc)
            {
                return EcmDecodeResult.Failed(
                    $"the ECM file's checksum does not match the data it decoded to (expected {expectedEdc.ToString("X8", CultureInfo.InvariantCulture)}, got {runningEdc.ToString("X8", CultureInfo.InvariantCulture)}), so the file is damaged.");
            }

            output.Flush();

            return EcmDecodeResult.Succeeded(destinationPath, written);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EcmDecodeResult.Failed($"the ECM file could not be decoded: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a block's leading number: two bits of kind and a count spread over as many 7-bit
    /// continuation bytes as it needs.
    /// </summary>
    private static bool TryReadBlockHeader(Stream input, out int type, out uint count)
    {
        type = 0;
        count = 0;

        var first = input.ReadByte();
        if (first < 0)
        {
            return false;
        }

        type = first & 3;
        count = (uint)((first >> 2) & 0x1F);
        var bits = 5;
        var current = first;

        while ((current & 0x80) != 0)
        {
            current = input.ReadByte();
            if (current < 0)
            {
                return false;
            }

            // A fifth continuation byte cannot contribute to a 32-bit count, and shifting by 32 or
            // more would wrap around and silently corrupt it.
            if (bits >= 32)
            {
                return false;
            }

            count |= (uint)(current & 0x7F) << bits;
            bits += 7;
        }

        return true;
    }

    private static bool TryCopyLiteral(Stream input, Stream output, byte[] buffer, uint count, ref uint runningEdc,
        ref long written)
    {
        var remaining = count;

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, (uint)buffer.Length);
            if (input.ReadAtLeast(buffer.AsSpan(0, chunk), chunk, throwOnEndOfStream: false) < chunk)
            {
                return false;
            }

            runningEdc = CdSectorEccEdc.ComputeEdc(runningEdc, buffer.AsSpan(0, chunk));
            output.Write(buffer, 0, chunk);
            written += chunk;
            remaining -= (uint)chunk;
        }

        return true;
    }

    private static bool TryExpandMode1(Stream input, Stream output, byte[] sector, uint count, ref uint runningEdc,
        ref long written)
    {
        for (uint i = 0; i < count; i++)
        {
            CdSectorEccEdc.WriteSyncAndMode(sector, mode: 0x01);

            // The 3-byte MSF address, then 2048 bytes of user data. Everything else is regenerated.
            if (!TryReadExactly(input, sector.AsSpan(0x00C, 0x003)) ||
                !TryReadExactly(input, sector.AsSpan(0x010, 0x800)))
            {
                return false;
            }

            CdSectorEccEdc.GenerateMode1(sector);

            runningEdc = CdSectorEccEdc.ComputeEdc(runningEdc, sector);
            output.Write(sector, 0, CdSectorEccEdc.SectorSize);
            written += CdSectorEccEdc.SectorSize;
        }

        return true;
    }

    /// <summary>
    /// Expands Mode 2 sectors. Both forms are stored, and written back, without their sync and
    /// header: a Mode 2 sector's parity is computed over a zeroed address precisely so that it stays
    /// valid in a 2336-byte-per-sector image, and the encoder relies on that.
    /// </summary>
    private static bool TryExpandMode2(Stream input, Stream output, byte[] sector, uint count, bool form1,
        ref uint runningEdc, ref long written)
    {
        var storedLength = form1 ? 0x804 : 0x918;

        for (uint i = 0; i < count; i++)
        {
            CdSectorEccEdc.WriteSyncAndMode(sector, mode: 0x02);

            if (!TryReadExactly(input, sector.AsSpan(0x014, storedLength)))
            {
                return false;
            }

            // The subheader is stored once but recorded twice in a real sector.
            sector.AsSpan(0x014, 4).CopyTo(sector.AsSpan(0x010, 4));

            if (form1)
            {
                CdSectorEccEdc.GenerateMode2Form1(sector);
            }
            else
            {
                CdSectorEccEdc.GenerateMode2Form2(sector);
            }

            runningEdc = CdSectorEccEdc.ComputeEdc(runningEdc, sector.AsSpan(0x010, CdSectorEccEdc.Mode2DataSize));
            output.Write(sector, 0x010, CdSectorEccEdc.Mode2DataSize);
            written += CdSectorEccEdc.Mode2DataSize;
        }

        return true;
    }

    private static bool TryReadExactly(Stream input, Span<byte> destination)
    {
        return input.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false) >= destination.Length;
    }

    private static EcmDecodeResult TruncatedFailure()
    {
        return EcmDecodeResult.Failed(
            "the ECM file ends part way through a block, so it is truncated. Re-download it and try again.");
    }

    private static EcmDecodeResult CorruptFailure()
    {
        return EcmDecodeResult.Failed(
            "the ECM file describes an implausibly large block, so its structure is damaged.");
    }
}