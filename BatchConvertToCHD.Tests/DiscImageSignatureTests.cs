using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class DiscImageSignatureTests : IDisposable
{
    private readonly string _tempDir;

    public DiscImageSignatureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DiscImageSignatureTests_{Guid.NewGuid():N}");
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

    private static byte[] CdSyncHeader()
    {
        var header = new byte[32];
        for (var i = 1; i <= 10; i++)
        {
            header[i] = 0xFF;
        }

        header[15] = 2;

        return header;
    }

    [Fact]
    public void RawCdSyncMarkIsRecognised()
    {
        Assert.Equal(DiscImageKind.RawCd, DiscImageSignature.Classify(CdSyncHeader()));
    }

    [Fact]
    public void AsciiSignaturesAreRecognised()
    {
        Assert.Equal(DiscImageKind.Rar, DiscImageSignature.Classify("Rar!\u001a\a"u8));
        Assert.Equal(
            DiscImageKind.AlcoholDescriptor,
            DiscImageSignature.Classify("MEDIA DESCRIPTOR"u8)
        );
        Assert.Equal(DiscImageKind.Isz, DiscImageSignature.Classify("IsZ!"u8));
        Assert.Equal(DiscImageKind.Cso, DiscImageSignature.Classify("CISO"u8));
        Assert.Equal(DiscImageKind.Cso, DiscImageSignature.Classify("ZISO"u8));
        Assert.Equal(DiscImageKind.Chd, DiscImageSignature.Classify("MComprHD"u8));
    }

    [Fact]
    public void BinarySignaturesAreRecognised()
    {
        Assert.Equal(DiscImageKind.Zip, DiscImageSignature.Classify([0x50, 0x4B, 0x03, 0x04]));
        Assert.Equal(
            DiscImageKind.SevenZip,
            DiscImageSignature.Classify([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])
        );
        Assert.Equal(DiscImageKind.Ecm, DiscImageSignature.Classify("ECM\0"u8));
        Assert.Equal(DiscImageKind.Pbp, DiscImageSignature.Classify("\0PBP"u8));
    }

    [Fact]
    public void UnrecognisedAndTruncatedHeadersReportUnknown()
    {
        Assert.Equal(DiscImageKind.Unknown, DiscImageSignature.Classify([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Equal(DiscImageKind.Unknown, DiscImageSignature.Classify([]));
    }

    [Fact]
    public void CookedIsoDataIsNotMistakenForRawCd()
    {
        // A 2048-byte-sector image has no sync mark, so it must not be claimed as a CD.
        var header = new byte[32];
        Array.Fill(header, (byte)0x11);

        Assert.Equal(DiscImageKind.Unknown, DiscImageSignature.Classify(header));
    }

    [Fact]
    public void DetectReadsFromDiskAndSurvivesAMissingFile()
    {
        var path = Path.Combine(_tempDir, "image.rar");
        File.WriteAllBytes(path, CdSyncHeader());

        // The real case: named .rar, actually a disc image.
        Assert.Equal(DiscImageKind.RawCd, DiscImageSignature.Detect(path));
        Assert.Equal(
            DiscImageKind.Unknown,
            DiscImageSignature.Detect(Path.Combine(_tempDir, "missing.rar"))
        );
    }

    [Fact]
    public void ArchiveKindsAreIdentifiedAsArchives()
    {
        Assert.True(DiscImageSignature.IsArchive(DiscImageKind.Rar));
        Assert.True(DiscImageSignature.IsArchive(DiscImageKind.Zip));
        Assert.True(DiscImageSignature.IsArchive(DiscImageKind.SevenZip));
        Assert.False(DiscImageSignature.IsArchive(DiscImageKind.RawCd));
        Assert.False(DiscImageSignature.IsArchive(DiscImageKind.Isz));
        Assert.False(DiscImageSignature.IsArchive(DiscImageKind.Unknown));
    }

    [Fact]
    public void EveryKindHasADescription()
    {
        foreach (var kind in Enum.GetValues<DiscImageKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DiscImageSignature.Describe(kind)));
        }
    }
}