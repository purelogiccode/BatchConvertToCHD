using System.Security.Cryptography;
using BatchConvertToCHD.Utilities.Ecm;

namespace BatchConvertToCHD.Tests;

public class EcmImageDecoderTests : IDisposable
{
    private const int SectorSize = 2352;

    /// <summary>
    /// SHA1 of the image the fixture decodes to. Recorded from a run where Neill Corlett's own
    /// decoder produced the identical bytes, so this constant is the reference implementation's
    /// answer rather than this code's.
    /// </summary>
    private const string ExpectedSha1 = "C79042C9DF371FDED431F72B43DCBEDC4DEAEF11";

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ecm-sample.ecm"
    );

    private readonly string _tempDir;

    public EcmImageDecoderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"EcmImageDecoderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    #region The reference fixture

    [Fact]
    public async Task TheReferenceFixtureDecodesToTheExpectedImage()
    {
        // The fixture was encoded by Corlett's tool from the image BuildReferenceImage rebuilds, and
        // holds all three sector kinds plus a literal run. Matching it byte for byte is what proves
        // both the block parsing and the regenerated EDC and Reed-Solomon parity.
        Assert.True(File.Exists(FixturePath), $"fixture missing at {FixturePath}");

        var outputPath = Path.Combine(_tempDir, "decoded.bin");
        var result = await EcmImageDecoder.DecodeAsync(
            FixturePath,
            outputPath,
            _ => { },
            CancellationToken.None
        );

        Assert.True(result.Success, result.FailureReason);

        var decoded = await File.ReadAllBytesAsync(outputPath);
        Assert.Equal(BuildReferenceImage(), decoded);
        Assert.Equal(ExpectedSha1, Convert.ToHexString(SHA1.HashData(decoded)));
        Assert.Equal(decoded.Length, result.BytesWritten);
    }

    [Fact]
    public async Task TheFixtureCoversAllThreeSectorKinds()
    {
        // Guards the fixture itself: if it were ever regenerated from a simpler image, the Mode 2
        // branches could stop being exercised while the test above still passed.
        var blocks = await CountBlockTypesAsync(FixturePath);

        Assert.True(blocks[0] > 0, "no literal blocks");
        Assert.True(blocks[1] > 0, "no Mode 1 blocks");
        Assert.True(blocks[2] > 0, "no Mode 2 Form 1 blocks");
        Assert.True(blocks[3] > 0, "no Mode 2 Form 2 blocks");
    }

    [Fact]
    public async Task DecodingIsRepeatable()
    {
        var first = Path.Combine(_tempDir, "first.bin");
        var second = Path.Combine(_tempDir, "second.bin");

        await EcmImageDecoder.DecodeAsync(FixturePath, first, _ => { }, CancellationToken.None);
        await EcmImageDecoder.DecodeAsync(FixturePath, second, _ => { }, CancellationToken.None);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
    }

    #endregion

    #region Refusals

    [Fact]
    public async Task AFileWithoutTheSignatureIsReported()
    {
        var path = Path.Combine(_tempDir, "notecm.ecm");
        await File.WriteAllBytesAsync(path, new byte[512]);

        var result = await EcmImageDecoder.DecodeAsync(
            path,
            Path.Combine(_tempDir, "out.bin"),
            _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "not an ECM file",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ATruncatedFileIsReported()
    {
        var bytes = await File.ReadAllBytesAsync(FixturePath);
        var path = Path.Combine(_tempDir, "cut.ecm");
        await File.WriteAllBytesAsync(path, bytes.AsSpan(0, bytes.Length / 2).ToArray());

        var result = await EcmImageDecoder.DecodeAsync(
            path,
            Path.Combine(_tempDir, "out.bin"),
            _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "truncated",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AWrongTrailingChecksumIsReported()
    {
        // The trailer is the only end-to-end check that the regenerated parity and the recovered data
        // are right, so a mismatch has to fail rather than yield a plausible image.
        var bytes = await File.ReadAllBytesAsync(FixturePath);
        bytes[^1] ^= 0xFF;

        var path = Path.Combine(_tempDir, "badedc.ecm");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await EcmImageDecoder.DecodeAsync(
            path,
            Path.Combine(_tempDir, "out.bin"),
            _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "checksum does not match",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task CorruptSectorDataIsCaughtByTheChecksum()
    {
        var bytes = await File.ReadAllBytesAsync(FixturePath);
        bytes[bytes.Length / 2] ^= 0xFF;

        var path = Path.Combine(_tempDir, "damaged.ecm");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await EcmImageDecoder.DecodeAsync(
            path,
            Path.Combine(_tempDir, "out.bin"),
            _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task AMissingFileIsReportedRatherThanThrown()
    {
        var result = await EcmImageDecoder.DecodeAsync(
            Path.Combine(_tempDir, "nope.ecm"),
            Path.Combine(_tempDir, "out.bin"),
            _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    #endregion

    #region Output naming

    [Theory]
    [InlineData(@"D:\roms\Game (USA).bin.ecm", "Game (USA).bin")]
    [InlineData(@"D:\roms\Game.img.ECM", "Game.img")]
    [InlineData(@"D:\roms\Game.iso.ecm", "Game.iso")]
    public void TheEcmSuffixSimplyComesOff(string input, string expected)
    {
        Assert.Equal(expected, EcmImageDecoder.GetDecodedFileName(input));
    }

    [Fact]
    public void AFileNotNamedEcmStillGetsAUsableName()
    {
        Assert.Equal("Game.bin", EcmImageDecoder.GetDecodedFileName(@"D:\roms\Game.dat"));
    }

    #endregion

    /// <summary>
    /// Rebuilds the 12-sector image the fixture was encoded from: four Mode 1, four Mode 2 Form 1 and
    /// four Mode 2 Form 2 sectors, each with a correct address, EDC and parity.
    /// </summary>
    internal static byte[] BuildReferenceImage()
    {
        const int sectors = 12;
        var image = new byte[sectors * SectorSize];

        for (var lba = 0; lba < sectors; lba++)
        {
            var sector = image.AsSpan(lba * SectorSize, SectorSize);

            switch (lba)
            {
                case < 4:
                    CdSectorEccEdc.WriteSyncAndMode(sector, 0x01);
                    WriteAddress(sector, lba);
                    FillPayload(sector.Slice(0x010, 0x800), lba);
                    CdSectorEccEdc.GenerateMode1(sector);
                    break;
                case < 8:
                    CdSectorEccEdc.WriteSyncAndMode(sector, 0x02);
                    WriteAddress(sector, lba);
                    WriteSubheader(sector, form2: false);
                    FillPayload(sector.Slice(0x018, 0x800), lba);
                    CdSectorEccEdc.GenerateMode2Form1(sector);
                    break;
                default:
                    CdSectorEccEdc.WriteSyncAndMode(sector, 0x02);
                    WriteAddress(sector, lba);
                    WriteSubheader(sector, form2: true);
                    FillPayload(sector.Slice(0x018, 0x914), lba);
                    CdSectorEccEdc.GenerateMode2Form2(sector);
                    break;
            }
        }

        return image;
    }

    /// <summary>Counts the blocks of each kind in an ECM file, without decoding it.</summary>
    private static async Task<int[]> CountBlockTypesAsync(string path)
    {
        var counts = new int[4];
        var bytes = await File.ReadAllBytesAsync(path);
        var offset = 4;

        while (offset < bytes.Length)
        {
            var first = bytes[offset++];
            var type = first & 3;
            var count = (uint)((first >> 2) & 0x1F);
            var bits = 5;
            var current = first;

            while ((current & 0x80) != 0 && offset < bytes.Length)
            {
                current = bytes[offset++];
                count |= (uint)(current & 0x7F) << bits;
                bits += 7;
            }

            if (count == 0xFFFFFFFF)
            {
                break;
            }

            count++;
            counts[type]++;

            // Skip the block's stored bytes to reach the next header.
            var stored = type switch
            {
                0 => count,
                1 => count * 0x803,
                2 => count * 0x804,
                _ => count * 0x918,
            };

            offset += (int)stored;
        }

        return counts;
    }

    private static void WriteAddress(Span<byte> sector, int lba)
    {
        var absolute = lba + 150;
        sector[0x00C] = ToBcd(absolute / (60 * 75));
        sector[0x00D] = ToBcd(absolute / 75 % 60);
        sector[0x00E] = ToBcd(absolute % 75);
    }

    private static void WriteSubheader(Span<byte> sector, bool form2)
    {
        sector[0x010] = 0x00;
        sector[0x011] = 0x00;
        sector[0x012] = (byte)(form2 ? 0x28 : 0x08);
        sector[0x013] = 0x00;
        sector.Slice(0x010, 4).CopyTo(sector.Slice(0x014, 4));
    }

    private static void FillPayload(Span<byte> payload, int lba)
    {
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(lba * 31 + i * 7 + (i >> 5));
        }
    }

    private static byte ToBcd(int value)
    {
        return (byte)(value / 10 * 16 + value % 10);
    }
}
