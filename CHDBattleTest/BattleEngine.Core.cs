namespace CHDBattleTest;

public sealed partial class BattleEngine
{
    private readonly BattleConfig _cfg;
    private readonly CancellationToken _ct;
    private readonly TextWriter _log;

    public BattleEngine(BattleConfig cfg, TextWriter log, CancellationToken ct)
    {
        _cfg = cfg;
        _log = log;
        _ct = ct;
    }

    public async Task RunFileAsync(FileReport report, string workDir)
    {
        Directory.CreateDirectory(workDir);
        try
        {
            if (_cfg.Decode)
                await DecodePhaseAsync(report, workDir).ConfigureAwait(false);

            if (_cfg.Encode && report.SkippedReason is null)
                await EncodePhaseAsync(report, workDir).ConfigureAwait(false);
        }
        finally
        {
            if (!_cfg.KeepTemp)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    // ignored
                }
        }
    }

    internal async Task<ToolRunner.RunResult> RunTool(string toolKey, string battle, string args, FileReport report)
    {
        var exe = toolKey == "chdman" ? _cfg.ChdmanPath : _cfg.ChdSharpPath;
        if (_cfg.Verbose) Log($"     $ {Path.GetFileName(exe)} {args}");
        var r = await ToolRunner.RunAsync(exe, args, TimeSpan.FromMinutes(_cfg.TimeoutMinutes), _ct)
            .ConfigureAwait(false);
        if (r.ExitCode != 0 && !string.IsNullOrWhiteSpace(r.OutputTail))
            Log($"     !! {toolKey} {battle} failed (exit {r.ExitCode}): {LastLine(r.OutputTail)}");
        return r;
    }

    internal void AddOutcome(FileReport report, StepOutcome o)
    {
        report.Steps.Add(o);
    }

    internal void Log(string msg)
    {
        Console.WriteLine(msg);
        _log.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");
        _log.Flush();
    }

    internal static long FileLen(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    internal double? Mibs(double seconds, ulong bytes)
    {
        return seconds > 0.001 && bytes > 0 ? bytes / 1048576.0 / seconds : null;
    }

    internal static double? Ratio(ToolRunner.RunResult r, long outBytes, ulong logical)
    {
        return r.ExitCode == 0 && logical > 0 ? (double)outBytes / logical : null;
    }

    internal string? FailMsg(ToolRunner.RunResult r)
    {
        return r.ExitCode == 0 ? null
            : r.TimedOut ? $"timeout after {_cfg.TimeoutMinutes} min"
            : $"exit {r.ExitCode}: {LastLine(r.OutputTail)}";
    }

    internal static string? LastLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : lines[^1][..Math.Min(300, lines[^1].Length)];
    }

    internal static string ShortHash(string? hash)
    {
        return string.IsNullOrEmpty(hash) ? "-" : hash[..12];
    }

    internal static string FmtS(double seconds)
    {
        return seconds >= 60 ? $"{seconds / 60.0:F1}m" : $"{seconds:F1}s";
    }

    internal static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }

    internal static bool EnoughSpaceFor(string driveRoot, long neededBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(driveRoot)) ?? driveRoot;
            var di = new DriveInfo(root);
            return di.AvailableFreeSpace > neededBytes * 2 + 1_000_000_000L;
        }
        catch
        {
            return true;
        }
    }
}