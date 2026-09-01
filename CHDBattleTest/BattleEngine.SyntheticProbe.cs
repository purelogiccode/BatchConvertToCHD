namespace CHDBattleTest;

/// <summary>
///     Synthetic regression probe: a deterministic raw DVD image whose size is a multiple of
///     the 2,048-byte sector size but NOT of the 4,096-byte hunk size (33,556,480 B = 8,193
///     hunks with a 2,048-byte partial last hunk; the work-buffer ring wraps ~32 times).
///     <para>
///         This is the input class that exposed the stale work-buffer ring corruption in
///         CHDSharp: the eager stale-hunk pre-read fast-forwarded the ring-buffered raw reader,
///         every hunk was served from a stale window near EOF, and the whole image collapsed
///         into SELF references — producing a tiny, self-consistent CHD full of repeated
///         garbage that both <c>chdman verify</c> and <c>chdsharp verify</c> accepted (the
///         embedded hashes were computed over the same mis-read bytes). The corpus DVD CHDs
///         all have exact hunk-multiple logical sizes, so the decoded .iso artifacts fed to
///         the createdvd battle never exercised this path (see FailingParity.md).
///     </para>
///     <para>
///         The probe runs two encode battles: one with the battle codec (zstd) and one with
///         chdman's default codec list (<c>lzma,zlib,huff,flac</c> — the mix the app uses).
///         Both products are cross-verified and round-tripped with <c>extractraw</c> against
///         the probe source: a byte-parity mismatch alone can hide a self-consistent-but-wrong
///         product only if both encoders agree on the wrongness, which the round-trip check
///         makes impossible.
///     </para>
/// </summary>
public sealed partial class BattleEngine
{
    /// <summary>Probe image size: 2,048-aligned, not 4,096-aligned (partial last hunk).</summary>
    public const long ProbeBytes = 32L * 1024 * 1024 + 2048;

    /// <summary>Report/CSV name for the synthetic probe file row.</summary>
    public const string ProbeReportName = "SYNTHETIC partial-last-hunk DVD probe (33556480B)";

    /// <summary>
    ///     Writes the deterministic probe image: alternating pseudo-random and structured
    ///     (compressible) hunks, no 256-hunk periodicity, so SELF-dedup cannot mask a
    ///     mis-served hunk.
    /// </summary>
    internal static void WriteProbeImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[1 << 20];
        ulong state = 0x243F6A8885A308D3;
        long remaining = ProbeBytes;
        while (remaining > 0)
        {
            var n = (int)Math.Min(buf.Length, remaining);
            for (var i = 0; i < n; i += 4096)
            {
                var len = Math.Min(4096, n - i);
                if ((i / 4096) % 2 == 0)
                {
                    for (var j = 0; j < len; j++)
                    {
                        state ^= state << 13;
                        state ^= state >> 7;
                        state ^= state << 17;
                        buf[i + j] = (byte)(state >> 56);
                    }
                }
                else
                {
                    for (var j = 0; j < len; j++)
                        buf[i + j] = (byte)(j * 7 + (i / 4096));
                }
            }

            fs.Write(buf, 0, n);
            remaining -= n;
        }
    }

    /// <summary>Runs both probe encode battles inside <paramref name="work" />.</summary>
    internal async Task RunSyntheticProbeAsync(FileReport report, string work)
    {
        Directory.CreateDirectory(work);
        report.Kind = MediaKind.Dvd;
        report.Version = 5;
        report.LogicalBytes = ProbeBytes;

        Log("  [probe] synthetic partial-last-hunk DVD image (32 MiB + 2 KiB; last hunk 2048/4096 B)");
        var input = Path.Combine(work, "probe.iso");
        WriteProbeImage(input);
        var (srcHash, _) = await Hashing.Sha256FileAsync(input, _ct).ConfigureAwait(false);

        await ProbeEncodeBattleAsync(report, work, input, srcHash, _cfg.CodecRaw,
            "probe-createdvd:" + _cfg.CodecRaw).ConfigureAwait(false);
        await ProbeEncodeBattleAsync(report, work, input, srcHash, "lzma,zlib,huff,flac",
            "probe-createdvd:default").ConfigureAwait(false);

        if (!_cfg.KeepTemp)
            try
            {
                Directory.Delete(work, true);
            }
            catch
            {
                // ignored
            }
    }

    private async Task ProbeEncodeBattleAsync(
        FileReport report,
        string work,
        string input,
        string srcHash,
        string codec,
        string battle)
    {
        var np = $"-np {_cfg.Workers}";
        var tag = battle.Replace(':', '_');
        var mChd = Path.Combine(work, tag + "_m.chd");
        var sChd = Path.Combine(work, tag + "_s.chd");

        Log($"  [probe] {battle}: createdvd -c {codec}");
        var rm = await RunTool("chdman", battle,
            $"createdvd -i \"{input}\" -o \"{mChd}\" -c {codec} -f {np}", report).ConfigureAwait(false);
        var rs = await RunTool("chdsharp", battle,
            $"createdvd -i \"{input}\" -o \"{sChd}\" -c {codec} -f {np}", report).ConfigureAwait(false);

        var mh = rm.ExitCode == 0
            ? (await Hashing.Sha256FileAsync(mChd, _ct).ConfigureAwait(false)).Hash
            : null;
        var sh = rs.ExitCode == 0
            ? (await Hashing.Sha256FileAsync(sChd, _ct).ConfigureAwait(false)).Hash
            : null;

        AddOutcome(report, new StepOutcome(battle, "chdman", rm.ExitCode == 0, rm.Seconds, FileLen(mChd),
            mh, rm.ExitCode, Mibs(rm.Seconds, report.LogicalBytes),
            Ratio(rm.ExitCode == 0, FileLen(mChd), report.LogicalBytes), FailMsg(rm)));
        AddOutcome(report, new StepOutcome(battle, "chdsharp", rs.ExitCode == 0, rs.Seconds, FileLen(sChd),
            sh, rs.ExitCode, Mibs(rs.Seconds, report.LogicalBytes),
            Ratio(rs.ExitCode == 0, FileLen(sChd), report.LogicalBytes), FailMsg(rs)));

        var parity = mh is not null && sh is not null &&
                     string.Equals(mh, sh, StringComparison.OrdinalIgnoreCase);
        var knownGap = string.Equals(battle, "probe-createdvd:default", StringComparison.OrdinalIgnoreCase)
            ? " (known FLAC encoder divergence, see FailingParity.md)"
            : "";
        AddOutcome(report, new StepOutcome(battle + "-parity", "cross", parity, 0, 0,
            parity ? ShortHash(mh!) : null, 0, null, null,
            parity ? null : "products not byte-identical" + knownGap));

        Log(parity
            ? $"     probe {battle}: chdman {FmtS(rm.Seconds)} vs chdsharp {FmtS(rs.Seconds)} - products BYTE-IDENTICAL ({ShortHash(mh)})"
            : $"     probe {battle}: BYTE-PARITY MISMATCH (chdman={ShortHash(mh)} chdsharp={ShortHash(sh)}){knownGap}");

        // round-trip: decode each product back to raw bytes and compare against the probe
        // source. A self-consistent but wrong CHD (the stale-ring bug's signature: both
        // verifiers pass on garbage data) fails here even if both encoders somehow agreed.
        if (rm.ExitCode == 0)
            await ProbeRoundTripAsync(report, battle, mChd, "chdman", srcHash, work).ConfigureAwait(false);
        if (rs.ExitCode == 0)
            await ProbeRoundTripAsync(report, battle, sChd, "chdsharp", srcHash, work).ConfigureAwait(false);

        if (rm.ExitCode == 0) await CrossVerifyAsync(report, battle, mChd, "chdman").ConfigureAwait(false);
        if (rs.ExitCode == 0) await CrossVerifyAsync(report, battle, sChd, "chdsharp").ConfigureAwait(false);

        if (!_cfg.KeepTemp)
        {
            foreach (var p in new[] { mChd, sChd })
                try
                {
                    File.Delete(p);
                }
                catch
                {
                    // ignored
                }
        }
    }

    private async Task ProbeRoundTripAsync(
        FileReport report,
        string battle,
        string product,
        string tool,
        string srcHash,
        string work)
    {
        var outRaw = Path.Combine(work, Sanitize($"{battle}_{tool}_rt") + ".bin");
        var rr = await RunTool(tool, battle + ":roundtrip",
            $"extractraw -i \"{product}\" -o \"{outRaw}\" -f", report).ConfigureAwait(false);
        var rh = rr.ExitCode == 0
            ? (await Hashing.Sha256FileAsync(outRaw, _ct).ConfigureAwait(false)).Hash
            : null;
        var ok = rr.ExitCode == 0 && rh is not null &&
                 string.Equals(rh, srcHash, StringComparison.OrdinalIgnoreCase);
        AddOutcome(report, new StepOutcome($"{battle}:roundtrip", tool, ok, rr.Seconds, FileLen(outRaw),
            rh, rr.ExitCode, null, null,
            ok ? null : rr.ExitCode == 0 ? "decoded data differs from the source image" : FailMsg(rr)));
        Log(ok
            ? $"     probe {battle}: {tool} round-trip MATCH ({ShortHash(rh)})"
            : $"     probe {battle}: {tool} ROUND-TRIP FAILURE << decoded data is not the source image");

        if (!_cfg.KeepTemp)
            try
            {
                File.Delete(outRaw);
            }
            catch
            {
                // ignored
            }
    }
}
