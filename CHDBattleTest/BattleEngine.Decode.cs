using System.Diagnostics;

namespace CHDBattleTest;

public sealed partial class BattleEngine
{
    private async Task DecodePhaseAsync(FileReport report, string work)
    {
        Log("  [decode] extractraw battle");
        string mDir = Path.Combine(work, "m_raw");
        string sDir = Path.Combine(work, "s_raw");
        Directory.CreateDirectory(mDir);
        Directory.CreateDirectory(sDir);

        string mRaw = Path.Combine(mDir, "raw.bin");
        string sRaw = Path.Combine(sDir, "raw.bin");

        var rm = await RunTool("chdman", "extractraw",
            $"extractraw -i \"{report.SourcePath}\" -o \"{mRaw}\" -f", report).ConfigureAwait(false);
        var rs = await RunTool("chdsharp", "extractraw",
            $"extractraw -i \"{report.SourcePath}\" -o \"{sRaw}\" -f", report).ConfigureAwait(false);

        string? mHash = null, sHash = null;
        if (rm.ExitCode == 0)
            (mHash, _) = await Hashing.Sha256FileAsync(mRaw, _ct).ConfigureAwait(false);
        if (rs.ExitCode == 0)
            (sHash, _) = await Hashing.Sha256FileAsync(sRaw, _ct).ConfigureAwait(false);

        AddOutcome(report, new StepOutcome("extractraw", "chdman", rm.ExitCode == 0, rm.Seconds,
            FileLen(mRaw), mHash, rm.ExitCode, Mibs(rm.Seconds, report.LogicalBytes), null, FailMsg(rm)));
        AddOutcome(report, new StepOutcome("extractraw", "chdsharp", rs.ExitCode == 0, rs.Seconds,
            FileLen(sRaw), sHash, rs.ExitCode, Mibs(rs.Seconds, report.LogicalBytes), null, FailMsg(rs)));

        bool parity = mHash is not null && sHash is not null &&
                      string.Equals(mHash, sHash, StringComparison.OrdinalIgnoreCase);
        string? parityErr = parity
            ? null
            : (rm.ExitCode == 0 && rs.ExitCode == 0 && FileLen(mRaw) != FileLen(sRaw))
                ? $"output format differs (chdman={FileLen(mRaw)} B vs chdsharp={FileLen(sRaw)} B)"
                : "decoded outputs differ";
        AddOutcome(report, new StepOutcome("extractraw-parity", "cross", parity, 0,
            0, parity ? ShortHash(mHash!) : null, 0, null, null, parityErr));

        Log(parity
            ? $"     extractraw: chdman {FmtS(rm.Seconds)} vs chdsharp {FmtS(rs.Seconds)} - MATCH ({ShortHash(mHash)})"
            : $"     extractraw: PARITY FAILURE chdman={(mHash is null ? "fail" : ShortHash(mHash))} chdsharp={(sHash is null ? "fail" : ShortHash(sHash))}");

        if (_cfg.LibDecode && report.Kind != MediaKind.LaserDisc)
        {
            string lRaw = Path.Combine(work, "lib_raw.bin");
            var sw = Stopwatch.StartNew();
            try
            {
                await Hashing.LibDecodeAsync(report.SourcePath, lRaw, _ => { }, _ct).ConfigureAwait(false);
                sw.Stop();
                AddOutcome(report, new StepOutcome("decode-lib", "chdsharp-lib", true, sw.Elapsed.TotalSeconds,
                    FileLen(lRaw), null, 0, Mibs(sw.Elapsed.TotalSeconds, report.LogicalBytes), null, null));
                Log($"     lib-decode: chdsharp in-process {FmtS(sw.Elapsed.TotalSeconds)}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                AddOutcome(report, new StepOutcome("decode-lib", "chdsharp-lib", false, sw.Elapsed.TotalSeconds,
                    FileLen(lRaw), null, -1, null, null, ex.Message));
            }

            try
            {
                File.Delete(lRaw);
            }
            catch
            {
                // ignored
            }
        }

        await StructuredExtractBattleAsync(report, work).ConfigureAwait(false);
    }

    private async Task StructuredExtractBattleAsync(FileReport report, string work)
    {
        if (!_cfg.IncludeAv && report.Kind == MediaKind.LaserDisc)
        {
            Log("     structured extract: laserdisc skipped (enable --include-av)");
            return;
        }

        (string cmd, string outName) = report.Kind switch
        {
            MediaKind.Cd => ("extractcd", "disc.cue"),
            MediaKind.GdRom => ("extractcd", "disc.gdi"),
            MediaKind.Dvd => ("extractdvd", "disc.iso"),
            MediaKind.Hdd => ("extracthd", "disc.img"),
            MediaKind.LaserDisc => ("extractld", "disc.avi"),
            _ => ("", "")
        };

        if (cmd.Length == 0)
        {
            Log($"     structured extract: skipped (kind={report.Kind})");
            return;
        }

        Log($"  [decode] {cmd} battle");
        string mDir = Path.Combine(work, "m_struct");
        string sDir = Path.Combine(work, "s_struct");
        Directory.CreateDirectory(mDir);
        Directory.CreateDirectory(sDir);

        string mOut = Path.Combine(mDir, outName);
        string sOut = Path.Combine(sDir, outName);

        var rm = await RunTool("chdman", cmd,
            $"{cmd} -i \"{report.SourcePath}\" -o \"{mOut}\" -f", report).ConfigureAwait(false);
        var rs = await RunTool("chdsharp", cmd,
            $"{cmd} -i \"{report.SourcePath}\" -o \"{sOut}\" -f", report).ConfigureAwait(false);

        string? mHash = null, sHash = null;
        long mBytes = 0, sBytes = 0;
        if (rm.ExitCode == 0)
            (mHash, mBytes) = await Hashing.Sha256DirectoryAsync(mDir, _ct).ConfigureAwait(false);
        if (rs.ExitCode == 0)
            (sHash, sBytes) = await Hashing.Sha256DirectoryAsync(sDir, _ct).ConfigureAwait(false);

        ulong denom = (ulong)Math.Max(mBytes, sBytes);
        AddOutcome(report, new StepOutcome(cmd, "chdman", rm.ExitCode == 0, rm.Seconds, mBytes, mHash, rm.ExitCode,
            Mibs(rm.Seconds, denom), null, FailMsg(rm)));
        AddOutcome(report, new StepOutcome(cmd, "chdsharp", rs.ExitCode == 0, rs.Seconds, sBytes, sHash, rs.ExitCode,
            Mibs(rs.Seconds, denom), null, FailMsg(rs)));

        bool parity = mHash is not null && sHash is not null &&
                      string.Equals(mHash, sHash, StringComparison.OrdinalIgnoreCase);
        bool formatDiff = !parity && rm.ExitCode == 0 && rs.ExitCode == 0 && mBytes != sBytes;
        string? parityErr = parity
            ? null
            : formatDiff
                ? $"output convention differs (chdman={mBytes} B vs chdsharp={sBytes} B total)"
                : "structured extraction outputs differ";
        AddOutcome(report, new StepOutcome(cmd + "-parity", "cross", parity, 0, 0,
            parity ? ShortHash(mHash!) : null, 0, null, null, parityErr));
        Log(parity
            ? $"     {cmd}: chdman {FmtS(rm.Seconds)} vs chdsharp {FmtS(rs.Seconds)} - MATCH"
            : formatDiff
                ? $"     {cmd}: FORMAT DIFFERENCE (chdman {mBytes} B vs chdsharp {sBytes} B)"
                : $"     {cmd}: PARITY FAILURE");

        if (!_cfg.KeepTemp)
        {
            if (rm.ExitCode == 0)
            {
                try
                {
                    Directory.Delete(sDir, true);
                }
                catch
                {
                    // ignored
                }
            }
            else
            {
                try
                {
                    Directory.Delete(mDir, true);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}