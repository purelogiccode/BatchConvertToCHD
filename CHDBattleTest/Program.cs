using System.Diagnostics;

namespace CHDBattleTest;

internal static class Program
{
    private static bool _cfgKeepWork;

    private static async Task<int> Main(string[] args)
    {
        var cfg = new BattleConfig();
        try
        {
            ParseArgs(args, cfg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            PrintUsage();
            return 2;
        }

        var baseDir = AppContext.BaseDirectory;
        cfg.ChdmanPath = Path.Combine(baseDir, "chdman.exe");
        cfg.ChdSharpPath = Path.Combine(baseDir, "CHDSharp.exe");
        if (!File.Exists(cfg.ChdmanPath) || !File.Exists(cfg.ChdSharpPath))
        {
            Console.Error.WriteLine("tool executables not found next to chdbattle (chdman.exe / CHDSharp.exe)");
            return 2;
        }

        if (!Directory.Exists(cfg.InputDir))
        {
            Console.Error.WriteLine($"input directory not found: {cfg.InputDir}");
            return 2;
        }

        if (string.IsNullOrEmpty(cfg.OutputRoot))
            cfg.OutputRoot = Path.Combine(AppContext.BaseDirectory, "BattleResults",
                DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(cfg.OutputRoot);
        Directory.CreateDirectory(cfg.WorkRoot);

        await using var log = new StreamWriter(cfg.LogPath, false);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            // ReSharper disable once AccessToDisposedClosure
            cts.Cancel();
        };

        var files = DiscoverFiles(cfg);
        if (files.Count == 0)
        {
            Console.WriteLine($"no .chd files matching '{cfg.Filter}' in {cfg.InputDir}");
            return 1;
        }

        Console.WriteLine($"chdbattle: {files.Count} candidate file(s) from {cfg.InputDir}");
        Console.WriteLine($"  results: {cfg.OutputRoot}");

        if (cfg.ListOnly)
        {
            Console.WriteLine();
            Console.WriteLine($"{"file",-70} {"size MiB",10} {"logical MiB",12} kind");
            foreach (var fi in files)
            {
                var insp = Classifier.Inspect(fi.FullName);
                var kind = !insp.IsChd ? "NOT A CHD" : $"{insp.Kind} V{insp.Version}";
                Console.WriteLine(
                    $"{fi.Name,-70} {fi.Length / 1048576.0,10:F1} {insp.LogicalBytes / 1048576.0,12:F1} {kind}");
            }

            return 0;
        }

        var reports = new List<FileReport>();
        var completed = cfg.Resume
            ? ReportWriter.LoadCompletedKeys(cfg.CsvPath)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long doneBytes = 0;
        // ReSharper disable once UnusedVariable
        var totalBytes = files.Sum(f => f.Length);
        var overallSw = Stopwatch.StartNew();

        foreach (var fi in files)
        {
            cts.Token.ThrowIfCancellationRequested();

            var report = new FileReport
            {
                FileName = fi.Name,
                SourcePath = fi.FullName,
                ChdBytes = fi.Length
            };
            reports.Add(report);

            var insp = Classifier.Inspect(fi.FullName);
            report.Version = insp.Version;
            report.Kind = insp.Kind;
            report.LogicalBytes = insp.LogicalBytes;

            var kindTag = insp.IsChd ? $"{insp.Kind} V{insp.Version}" : "NOT A CHD";
            Console.WriteLine();
            Console.WriteLine($"[{reports.IndexOf(report) + 1}/{files.Count}] {fi.Name}");
            Console.WriteLine(
                $"  chd={fi.Length / 1048576.0:F1} MiB logical={insp.LogicalBytes / 1048576.0:F1} MiB kind={kindTag}");

            if (!insp.IsChd)
            {
                report.SkippedReason = insp.Error ?? "not a chd";
                ReportWriter.AppendCsv(cfg.CsvPath, report);
                continue;
            }

            if (completed.Contains(fi.Name))
            {
                report.SkippedReason = "resume: already in csv";
                Console.WriteLine("  skipped (resume)");
                continue;
            }

            if (insp.LogicalBytes == 0 && insp.Kind == MediaKind.Unknown && insp.Error is not null)
            {
                report.SkippedReason = insp.Error;
                ReportWriter.AppendCsv(cfg.CsvPath, report);
                continue;
            }

            if (!BattleEngine.EnoughSpaceFor(cfg.OutputRoot, (long)insp.LogicalBytes * 3))
            {
                report.SkippedReason = "skipped: insufficient free disk space";
                Console.WriteLine($"  !! {report.SkippedReason}");
                ReportWriter.AppendCsv(cfg.CsvPath, report);
                continue;
            }

            var work = Path.Combine(cfg.WorkRoot,
                BattleEngine.Sanitize(fi.Name) + "_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var engine = new BattleEngine(cfg, log, cts.Token);
                await engine.RunFileAsync(report, work).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                report.SkippedReason = "cancelled";
                ReportWriter.AppendCsv(cfg.CsvPath, report);
                Console.WriteLine("cancelled by user");
                break;
            }
            catch (Exception ex)
            {
                report.SkippedReason = "engine error: " + ex.Message;
                Console.WriteLine($"  !! engine error: {ex.Message}");
            }

            ReportWriter.AppendCsv(cfg.CsvPath, report);
            doneBytes += fi.Length;
            Console.WriteLine(
                $@"  >> progress: {doneBytes / 1048576.0:0} MiB done, elapsed {overallSw.Elapsed:hh\:mm\:ss}");
        }

        Console.WriteLine();
        Console.WriteLine("writing markdown report...");
        ReportWriter.WriteMarkdown(cfg.MdPath, cfg.InputDir, reports, cfg);
        Console.WriteLine($"csv:     {cfg.CsvPath}");
        Console.WriteLine($"report:  {cfg.MdPath}");
        Console.WriteLine($@"total time: {overallSw.Elapsed:hh\:mm\:ss}");

        if (!_cfgKeepWork)
            try
            {
                Directory.Delete(cfg.WorkRoot, true);
            }
            catch
            {
                // ignored
            }

        return 0;
    }

    private static List<FileInfo> DiscoverFiles(BattleConfig cfg)
    {
        var dir = new DirectoryInfo(cfg.InputDir);
        var all = dir.EnumerateFiles(cfg.Filter, SearchOption.TopDirectoryOnly).ToList();
        if (cfg.MaxMb > 0)
            all = all.Where(f => f.Length <= cfg.MaxMb * 1048576L).ToList();
        if (cfg.MinMb > 0)
            all = all.Where(f => f.Length >= cfg.MinMb * 1048576L).ToList();
        all = all.OrderBy(f => f.Length, Comparer<long>.Create((a, b) => a.CompareTo(b))).ToList();
        if (cfg.MaxFiles > 0)
            all = all.Take(cfg.MaxFiles).ToList();
        return all;
    }

    private static void ParseArgs(string[] args, BattleConfig cfg)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-i":
                case "--in": cfg.InputDir = Next(); break;
                case "-o":
                case "--out": cfg.OutputRoot = Next(); break;
                case "--filter": cfg.Filter = Next(); break;
                case "--max-files": cfg.MaxFiles = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--min-mb": cfg.MinMb = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--max-mb": cfg.MaxMb = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--codec-raw": cfg.CodecRaw = Next(); break;
                case "--codec-cd": cfg.CodecCd = Next(); break;
                case "--workers": cfg.Workers = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--phases":
                    foreach (var p in Next().Split(','))
                        if (p.Equals("decode", StringComparison.OrdinalIgnoreCase)) cfg.Decode = true;
                        else if (p.Equals("encode", StringComparison.OrdinalIgnoreCase)) cfg.Encode = true;
                        else throw new ArgumentException($"unknown phase '{p}'");

                    break;
                case "--no-decode": cfg.Decode = false; break;
                case "--no-encode": cfg.Encode = false; break;
                case "--include-av": cfg.IncludeAv = true; break;
                case "--lib-decode": cfg.LibDecode = true; break;
                case "--timeout-min": cfg.TimeoutMinutes = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--keep-temp":
                    cfg.KeepTemp = true;
                    _cfgKeepWork = true;
                    break;
                case "--resume": cfg.Resume = true; break;
                case "--list": cfg.ListOnly = true; break;
                case "-v":
                case "--verbose": cfg.Verbose = true; break;
                default: throw new ArgumentException($"unknown option '{a}'");
            }

            continue;

            string Next()
            {
                return i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
                          usage: chdbattle [options]
                            -i, --in <dir>         input directory with .chd files   (default H:\CHDTest)
                            -o, --out <dir>        results root directory
                                --filter <glob>    file filter                       (default *.chd)
                                --max-files N      limit number of files
                                --min-mb N         skip files smaller than N MiB
                                --max-mb N         skip files larger than N MiB
                                --codec-raw <c>    codec for copy/dvd/hd/raw battles (default zstd)
                                --codec-cd <c>     codec for cd/gd battles           (default cdzl)
                                --workers N        -np passed to both tools          (default cores)
                                --phases <list>    decode,encode                     (default both)
                                --no-decode | --no-encode
                                --include-av       enable laserdisc extract/create battles
                                --lib-decode       extra in-process CHDSharpLib decode measurement
                                --timeout-min N    per-process timeout               (default 45)
                                --keep-temp        keep work directories
                                --resume           skip files already present in results.csv
                                --list             classify only, run no battles
                                -v, --verbose      echo tool command lines and failures
                          """);
    }
}