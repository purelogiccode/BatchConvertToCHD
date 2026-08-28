namespace CHDBattleTest;

public sealed partial class BattleEngine
{
    private async Task EncodePhaseAsync(FileReport report, string work)
    {
        await CopyBattleAsync(report, work).ConfigureAwait(false);
        await CreateBattleAsync(report, work).ConfigureAwait(false);
    }

    private async Task CopyBattleAsync(FileReport report, string work)
    {
        var codec = _cfg.CodecRaw;
        var battle = "copy:" + codec;
        Log($"  [encode] copy/recompress battle (codec={codec})");

        var mChd = Path.Combine(work, "copy_m.chd");
        var sChd = Path.Combine(work, "copy_s.chd");
        var np = $"-np {_cfg.Workers}";

        var rm = await RunTool("chdman", battle,
            $"copy -i \"{report.SourcePath}\" -o \"{mChd}\" -c {codec} -f {np}", report).ConfigureAwait(false);
        var rs = await RunTool("chdsharp", battle,
            $"copy -i \"{report.SourcePath}\" -o \"{sChd}\" -c {codec} -f {np}", report).ConfigureAwait(false);

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
        var sizeMatch = rm.ExitCode == 0 && rs.ExitCode == 0 && FileLen(mChd) == FileLen(sChd);
        AddOutcome(report, new StepOutcome(battle + "-parity", "cross", parity, 0, 0,
            parity ? ShortHash(mh!) : null, 0, null, null,
            parity ? null : sizeMatch ? "same size, different bytes" : "products differ"));

        Log(parity
            ? $"     copy: chdman {FmtS(rm.Seconds)} vs chdsharp {FmtS(rs.Seconds)} - products BYTE-IDENTICAL ({ShortHash(mh)})"
            : $"     copy: chdman={OkFail(rm)} chdsharp={OkFail(rs)} byte-parity={(parity ? "yes" : "NO")} ({(mh is null ? "-" : ShortHash(mh))} vs {(sh is null ? "-" : ShortHash(sh))})");

        if (rm.ExitCode == 0) await CrossVerifyAsync(report, battle, mChd, "chdman").ConfigureAwait(false);
        if (rs.ExitCode == 0) await CrossVerifyAsync(report, battle, sChd, "chdsharp").ConfigureAwait(false);

        if (!_cfg.KeepTemp)
        {
            try
            {
                File.Delete(mChd);
            }
            catch
            {
                // ignored
            }

            try
            {
                File.Delete(sChd);
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task CreateBattleAsync(FileReport report, string work)
    {
        // ReSharper disable once UnusedVariable
        var (cmd, input, outName) = report.Kind switch
        {
            MediaKind.Cd => ("createcd", FindStructuredArtifact(work, ".cue"), "create_m_s.chd"),
            MediaKind.GdRom => ("createcd", FindStructuredArtifact(work, ".gdi"), "create_m_s.chd"),
            MediaKind.Dvd => ("createdvd", FindStructuredArtifact(work, ".iso"), "create_m_s.chd"),
            MediaKind.Hdd => ("createhd", FindStructuredArtifact(work, ".img"), "create_m_s.chd"),
            MediaKind.LaserDisc when _cfg.IncludeAv => ("createld", FindStructuredArtifact(work, ".avi"),
                "create_m_s.chd"),
            _ => ("createraw", null, "create_m_s.chd")
        };

        if (!_cfg.IncludeAv && report.Kind == MediaKind.LaserDisc)
        {
            Log("     create battle: laserdisc skipped (enable --include-av)");
            return;
        }

        if (report.Kind is MediaKind.Cd or MediaKind.GdRom or MediaKind.Dvd or MediaKind.Hdd or MediaKind.LaserDisc)
        {
            if (input is null)
            {
                Log($"     create battle: SKIPPED - no decoded artifact available for {cmd}");
                return;
            }

            Log($"  [encode] {cmd} battle from decoded artifact");
        }
        else
        {
            input = FindRawArtifact(work);
            if (input is null)
            {
                Log("     create battle: SKIPPED - raw decode artifact missing for createraw");
                return;
            }

            Log("  [encode] createraw battle from decoded raw image");
        }

        var codec = _cfg.CodecFor(report.Kind);
        var mChd = Path.Combine(work, "create_m.chd");
        var sChd = Path.Combine(work, "create_s.chd");
        var np = $"-np {_cfg.Workers}";
        var battle = cmd + ":" + codec;

        var rm = await RunTool("chdman", battle,
            $"{cmd} -i \"{input}\" -o \"{mChd}\" -c {codec} -f {np}", report).ConfigureAwait(false);
        var rs = await RunTool("chdsharp", battle,
            $"{cmd} -i \"{input}\" -o \"{sChd}\" -c {codec} -f {np}", report).ConfigureAwait(false);

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
        var sizeMatch = rm.ExitCode == 0 && rs.ExitCode == 0 && FileLen(mChd) == FileLen(sChd);
        AddOutcome(report, new StepOutcome(battle + "-parity", "cross", parity, 0, 0,
            parity ? ShortHash(mh!) : null, 0, null, null,
            parity ? null : sizeMatch ? "same size, different bytes" : "products differ"));

        Log(parity
            ? $"     {cmd}: chdman {FmtS(rm.Seconds)} vs chdsharp {FmtS(rs.Seconds)} - products BYTE-IDENTICAL ({ShortHash(mh)})"
            : $"     {cmd}: chdman={OkFail(rm)} chdsharp={OkFail(rs)} byte-parity={(parity ? "yes" : "NO")}");

        if (rm.ExitCode == 0) await CrossVerifyAsync(report, battle, mChd, "chdman").ConfigureAwait(false);
        if (rs.ExitCode == 0) await CrossVerifyAsync(report, battle, sChd, "chdsharp").ConfigureAwait(false);

        if (!_cfg.KeepTemp)
        {
            try
            {
                File.Delete(mChd);
            }
            catch
            {
                // ignored
            }

            try
            {
                File.Delete(sChd);
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task CrossVerifyAsync(FileReport report, string battle, string product, string producer)
    {
        var rv = await RunTool("chdman", battle + ":verify",
            $"verify -i \"{product}\"", report).ConfigureAwait(false);
        AddOutcome(report, new StepOutcome($"{battle}:verify-chdman[{producer}-product]", "chdman",
            rv.ExitCode == 0, rv.Seconds, 0, null, rv.ExitCode, null, null, FailMsg(rv)));

        var sv = await RunTool("chdsharp", battle + ":verify",
            $"verify -i \"{product}\"", report).ConfigureAwait(false);
        AddOutcome(report, new StepOutcome($"{battle}:verify-chdsharp[{producer}-product]", "chdsharp",
            sv.ExitCode == 0, sv.Seconds, 0, null, sv.ExitCode, null, null, FailMsg(sv)));

        var agree = rv.ExitCode == 0 && sv.ExitCode == 0;
        Log(
            $"     verify {producer} product: chdman={Ok(rv.ExitCode == 0)} chdsharp={Ok(sv.ExitCode == 0)}{(agree ? "" : "  << VERIFIERS DISAGREE")}");
    }

    private static string? FindStructuredArtifact(string work, string extension)
    {
        foreach (var sub in new[] { "m_struct", "s_struct" })
        {
            var dir = Path.Combine(work, sub);
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir, "*" + extension, SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) return hit;
        }

        return null;
    }

    private static string? FindRawArtifact(string work)
    {
        foreach (var sub in new[] { "m_raw", "s_raw" })
        {
            var candidate = Path.Combine(work, sub, "raw.bin");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static double? Ratio(bool ok, long outBytes, ulong logical)
    {
        return ok && logical > 0 ? (double)outBytes / logical : null;
    }

    private static string OkFail(ToolRunner.RunResult r)
    {
        return r.ExitCode == 0 ? "ok" : "FAIL";
    }

    private static string Ok(bool b)
    {
        return b ? "ok" : "FAIL";
    }
}