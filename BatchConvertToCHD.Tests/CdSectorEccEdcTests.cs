using BatchConvertToCHD.Utilities.Ecm;

namespace BatchConvertToCHD.Tests;

public class CdSectorEccEdcTests
{
    [Fact]
    public void SyncPatternAndModeMatchARealSector()
    {
        var sector = new byte[CdSectorEccEdc.SectorSize];
        sector.AsSpan().Fill(0xAA);

        CdSectorEccEdc.WriteSyncAndMode(sector, 0x02);

        // 00 followed by ten FF and a 00 is the mark every raw CD sector opens with.
        Assert.Equal(0x00, sector[0]);
        for (var i = 1; i <= 10; i++) Assert.Equal(0xFF, sector[i]);

        Assert.Equal(0x00, sector[11]);
        Assert.Equal(0x02, sector[0x0F]);

        // Everything else is cleared, so a reused buffer cannot leak the previous sector.
        Assert.All(sector[0x10..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void EdcOfNothingIsZeroAndIsOrderDependent()
    {
        Assert.Equal(0u, CdSectorEccEdc.ComputeEdc(0, []));

        var forward = CdSectorEccEdc.ComputeEdc(0, [1, 2, 3, 4]);
        var reversed = CdSectorEccEdc.ComputeEdc(0, [4, 3, 2, 1]);

        Assert.NotEqual(forward, reversed);
    }

    [Fact]
    public void EdcAccumulatesTheSameWhetherFedWholeOrInPieces()
    {
        // The ECM trailer is checked against an EDC accumulated across the whole image in chunks, so
        // a partial fold has to equal the single-shot result.
        var data = new byte[1000];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 13);

        var whole = CdSectorEccEdc.ComputeEdc(0, data);

        var pieces = CdSectorEccEdc.ComputeEdc(0, data.AsSpan(0, 137));
        pieces = CdSectorEccEdc.ComputeEdc(pieces, data.AsSpan(137, 500));
        pieces = CdSectorEccEdc.ComputeEdc(pieces, data.AsSpan(637));

        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void Mode1FillsEdcIntermediateAndBothParityFields()
    {
        var sector = BuildSector(0x01, 0x010, 0x800, 100);

        CdSectorEccEdc.GenerateMode1(sector);

        // EDC at 0x810, eight zero bytes at 0x814, P parity at 0x81C, Q parity at 0x8C8.
        Assert.True(sector.AsSpan(0x810, 4).ContainsAnyExcept((byte)0));
        Assert.All(sector[0x814..0x81C], b => Assert.Equal(0, b));
        Assert.True(sector.AsSpan(0x81C, 172).ContainsAnyExcept((byte)0));
        Assert.True(sector.AsSpan(0x8C8, 104).ContainsAnyExcept((byte)0));
    }

    [Fact]
    public void Mode1ParityCoversTheAddress()
    {
        // Mode 1 parity is computed over the address as written, so two sectors differing only in
        // their address must get different parity. Getting this backwards is a silent corruption.
        var first = BuildSector(0x01, 0x010, 0x800, 100);
        var second = (byte[])first.Clone();
        second[0x00E] = (byte)(second[0x00E] + 1);

        CdSectorEccEdc.GenerateMode1(first);
        CdSectorEccEdc.GenerateMode1(second);

        Assert.False(first.AsSpan(0x81C, 172).SequenceEqual(second.AsSpan(0x81C, 172)));
    }

    [Fact]
    public void Mode2Form1ParityIgnoresTheAddressAndLeavesItIntact()
    {
        // Form 1 parity is computed over a zeroed address so it stays valid when the sector is read
        // without its header, which is exactly what ECM relies on.
        var first = BuildSector(0x02, 0x018, 0x800, 200);
        WriteSubheader(first);
        var second = (byte[])first.Clone();
        second[0x00E] = (byte)(second[0x00E] + 1);

        CdSectorEccEdc.GenerateMode2Form1(first);
        CdSectorEccEdc.GenerateMode2Form1(second);

        Assert.True(first.AsSpan(0x81C, 172).SequenceEqual(second.AsSpan(0x81C, 172)));
        Assert.True(first.AsSpan(0x8C8, 104).SequenceEqual(second.AsSpan(0x8C8, 104)));

        // The address must be put back after the parity pass.
        Assert.Equal((byte)(first[0x00E] + 1), second[0x00E]);
    }

    [Fact]
    public void Mode2Form2GetsAnEdcAndNoParity()
    {
        // Form 2 spends the parity bytes on user data instead, which is why it carries 2324 bytes.
        var sector = BuildSector(0x02, 0x018, 0x914, 300);
        WriteSubheader(sector);
        var before = (byte[])sector.Clone();

        CdSectorEccEdc.GenerateMode2Form2(sector);

        Assert.False(sector.AsSpan(0x92C, 4).SequenceEqual(before.AsSpan(0x92C, 4)));
        Assert.True(sector.AsSpan(0x81C, 172).SequenceEqual(before.AsSpan(0x81C, 172)));
    }

    [Fact]
    public void GeneratingTwiceIsStable()
    {
        var sector = BuildSector(0x01, 0x010, 0x800, 400);

        CdSectorEccEdc.GenerateMode1(sector);
        var once = (byte[])sector.Clone();
        CdSectorEccEdc.GenerateMode1(sector);

        Assert.Equal(once, sector);
    }

    private static byte[] BuildSector(byte mode, int payloadOffset, int payloadLength, int seed)
    {
        var sector = new byte[CdSectorEccEdc.SectorSize];
        CdSectorEccEdc.WriteSyncAndMode(sector, mode);

        sector[0x00C] = 0x00;
        sector[0x00D] = 0x02;
        sector[0x00E] = 0x10;

        for (var i = 0; i < payloadLength; i++) sector[payloadOffset + i] = (byte)(seed + i * 7);

        return sector;
    }

    private static void WriteSubheader(byte[] sector)
    {
        sector[0x010] = 0x00;
        sector[0x011] = 0x00;
        sector[0x012] = 0x08;
        sector[0x013] = 0x00;
        sector.AsSpan(0x010, 4).CopyTo(sector.AsSpan(0x014, 4));
    }
}