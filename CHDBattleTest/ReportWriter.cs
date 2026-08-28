using System.Globalization;
using System.Text;

namespace CHDBattleTest;

public static class ReportWriter
{
    private static readonly string[] CsvHeader =
    [
        "file", "kind", "version", "chd_mib", "logical_mib",
        "battle", "tool", "ok", "seconds", "mib_per_sec", "out_bytes", "ratio", "hash12", "exit", "error"
    ];

    public static void AppendCsv(string csvPath, FileReport report)
    {
        var newFile = !File.Exists(csvPath);
        var sb = new StringBuilder();
        if (newFile)
            sb.AppendLine(string.Join(',', CsvHeader));

        var file = Csv(report.FileName);
        var kind = report.Kind.ToString();
        foreach (var s in report.Steps)
            sb.Append(file).Append(',')
                .Append(kind).Append(',')
                .Append(report.Version).Append(',')
                .Append(Mib(report.ChdBytes)).Append(',')
                .Append(MibU(report.LogicalBytes)).Append(',')
                .Append(Csv(s.Battle)).Append(',')
                .Append(s.Tool).Append(',')
                .Append(s.Success ? "1" : "0").Append(',')
                .Append(s.Seconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(s.MibPerSecond?.ToString("F2", CultureInfo.InvariantCulture) ?? "")
                .Append(',')
                .Append(s.OutputBytes).Append(',')
                .Append(s.Ratio?.ToString("F4", CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(s.Hash ?? "").Append(',')
                .Append(s.ExitCode).Append(',')
                .AppendLine(Csv(s.Error ?? ""));

        File.AppendAllText(csvPath, sb.ToString());
    }

    public static HashSet<string> LoadCompletedKeys(string csvPath)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(csvPath)) return keys;
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            var i = line.IndexOf(',');
            if (i <= 0) continue;
            keys.Add(line[..i]);
        }

        return keys;
    }

    public static void WriteMarkdown(string mdPath, string inputDir, IReadOnlyList<FileReport> reports,
        BattleConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# chdman vs CHDSharp - Battleground Results");
        sb.AppendLine();
        sb.AppendLine($"- Input: `{inputDir}`");
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Files run: {reports.Count(r => r.SkippedReason is null)} / {reports.Count}");
        sb.AppendLine(
            $"- Workers (-np): {cfg.Workers} | Codecs: raw/copy/dvd/hd=`{cfg.CodecRaw}`, cd/gd=`{cfg.CodecCd}`");
        sb.AppendLine($"- chdman: `{cfg.ChdmanPath}` | CHDSharp: `{cfg.ChdSharpPath}`");
        sb.AppendLine();

        foreach (var g in reports.SelectMany(r => r.Steps)
                     .Where(s => s.Tool != "cross")
                     .GroupBy(s => s.Battle)
                     .OrderBy(g => g.Key))
            AppendBattleSummary(sb, g.Key, g.ToList());

        sb.AppendLine("## Parity (cross-tool agreement)");
        sb.AppendLine();
        sb.AppendLine("| battle | result | files |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in reports.SelectMany(r => r.Steps)
                     .Where(s => s.Tool == "cross")
                     .GroupBy(s => s.Battle)
                     .OrderBy(g => g.Key))
        {
            var ok = g.Count(s => s.Success);
            sb.AppendLine(
                $"| {g.Key} | {ok}/{g.Count()} {(ok == g.Count() ? "MATCH" : "MISMATCH/FAIL")} | {g.Count()} |");
        }

        sb.AppendLine();

        sb.AppendLine("## Per-file results");
        sb.AppendLine();
        foreach (var r in reports.OrderByDescending(r => r.LogicalBytes))
        {
            sb.AppendLine($"### {r.FileName}  ({MibU(r.LogicalBytes)} MiB logical, V{r.Version}, {r.Kind})");
            if (r.SkippedReason is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"- skipped: {r.SkippedReason}");
                continue;
            }

            sb.AppendLine();
            sb.AppendLine("| battle | tool | ok | seconds | MiB/s | out bytes | ratio | hash12 | error |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var s in r.Steps)
                sb.AppendLine($"| {s.Battle} | {s.Tool} | {(s.Success ? "OK" : "FAIL")} | " +
                              $"{(s.Seconds > 0 ? s.Seconds.ToString("F2") : "")} | " +
                              $"{s.MibPerSecond?.ToString("F1") ?? ""} | " +
                              $"{(s.OutputBytes > 0 ? s.OutputBytes : "")} | " +
                              $"{s.Ratio?.ToString("F4") ?? ""} | {s.Hash ?? ""} | " +
                              $"{(s.Error ?? "").Replace('|', '/')} |");

            sb.AppendLine();
        }

        sb.AppendLine("> Note: the CHDSharp CLI deep-verifies every encode with its own library before exiting; " +
                      "chdman does not verify. Encode timings therefore include that extra pass for CHDSharp.");
        File.WriteAllText(mdPath, sb.ToString());
    }

    private static void AppendBattleSummary(StringBuilder sb, string battle, List<StepOutcome> steps)
    {
        var byTool = steps.GroupBy(s => s.Tool).Where(g => g.Key != "cross").ToList();
        if (byTool.Count == 0) return;

        sb.AppendLine($"## Battle: {battle}");
        sb.AppendLine();
        sb.AppendLine("| tool | runs | ok | total sec | weighted MiB/s | wins (faster) |");
        sb.AppendLine("|---|---|---|---|---|---|");

        var stats = byTool.ToDictionary(
            g => g.Key,
            g =>
            {
                var secs = g.Sum(s => s.Seconds);
                var bytes = g.Sum(s => (double)s.OutputBytes);
                var wMibs = secs > 0 ? bytes / 1048576.0 / secs : 0;
                return (Runs: g.Count(), Ok: g.Count(s => s.Success), Secs: secs, Mibs: wMibs);
            });

        string? winner = null;
        var valid = stats.Where(kv => kv.Value.Ok > 0 && kv.Value.Mibs > 0).ToList();
        if (valid.Count == 2)
            winner = valid[0].Value.Mibs > valid[1].Value.Mibs ? valid[0].Key : valid[1].Key;

        foreach (var (tool, st) in stats.OrderBy(kv => kv.Key))
        {
            var wins = winner == tool && valid.Count == 2 ? st.Ok : 0;
            sb.AppendLine($"| {tool} | {st.Runs} | {st.Ok} | {st.Secs:F1} | {st.Mibs:F1} | {wins} |");
        }

        sb.AppendLine();
    }

    private static string Mib(long bytes)
    {
        return (bytes / 1048576.0).ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string MibU(ulong bytes)
    {
        return (bytes / 1048576.0).ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string Csv(string field)
    {
        if (field.Contains('"') || field.Contains(',') || field.Contains('\n'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }
}