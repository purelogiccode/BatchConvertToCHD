using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CHDBattleTest;

public static class ToolRunner
{
    public sealed record RunResult(int ExitCode, double Seconds, bool TimedOut, string OutputTail);

    public static async Task<RunResult> RunAsync(string exePath, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = new Process();
        proc.StartInfo = psi;
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) Append(sb, e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Append(sb, e.Data);
        };

        var sw = Stopwatch.StartNew();
        if (!proc.Start())
            return new RunResult(-1, 0, false, "failed to start process");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        sw.Stop();

        string tail = sb.Length > 8000 ? "..." + sb.ToString(sb.Length - 8000, 8000) : sb.ToString();
        return new RunResult(timedOut ? -9 : proc.ExitCode, sw.Elapsed.TotalSeconds, timedOut, tail.Replace('\0', ' '));
    }

    private static void Append(StringBuilder sb, string line)
    {
        if (sb.Length > 200_000) return;
        sb.AppendLine(line);
    }
}

public static class Hashing
{
    public static async Task<(string Hash, long Bytes)> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return (Convert.ToHexString(hash), fs.Length);
    }

    public static async Task<(string Hash, long Bytes)> Sha256DirectoryAsync(string dir, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetRelativePath(dir, f), StringComparer.Ordinal)
            .ToList();
        using var sha = SHA256.Create();
        long total = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            byte[] nameBytes = Encoding.UTF8.GetBytes(rel.ToLowerInvariant() + ":" + new FileInfo(f).Length + ":");
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            await using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buf = new byte[1024 * 1024];
            int read;
            while ((read = await fs.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buf, 0, read, null, 0);
                total += read;
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (Convert.ToHexString(sha.Hash ?? []), total);
    }

    public static async Task LibDecodeAsync(string chdPath, string outPath, Action<double> progress,
        CancellationToken ct)
    {
        var err = CHDSharp.ChdFile.Open(chdPath, out var chd);
        if (err != CHDSharp.Models.ChdError.Chderrnone)
            throw new InvalidOperationException($"ChdFile.Open failed: {err}");
        try
        {
            if (chd != null)
            {
                uint hunkBytes = chd.HunkBytes;
                ulong hunkCount = chd.HunkCount;
                byte[] buf = new byte[hunkBytes];
                await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                for (ulong i = 0; i < hunkCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var e2 = chd.ReadHunk((uint)i, buf);
                    if (e2 != CHDSharp.Models.ChdError.Chderrnone)
                        throw new InvalidOperationException($"ReadHunk({i}) failed: {e2}");
                    await fs.WriteAsync(buf.AsMemory(0, (int)Math.Min(hunkBytes, (ulong)buf.Length)), ct)
                        .ConfigureAwait(false);
                    if ((i & 0x3FF) == 0) progress(i * 100.0 / Math.Max(1UL, hunkCount));
                }
            }
        }
        finally
        {
            if (chd != null) await chd.DisposeAsync().ConfigureAwait(false);
        }
    }
}