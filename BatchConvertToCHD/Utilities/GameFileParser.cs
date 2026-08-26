using System.IO;
using System.Text;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Provides methods for parsing game file formats (CUE, GDI, TOC) to extract referenced files.
/// </summary>
internal static class GameFileParser
{
    private static readonly char[] Separator = [' ', '\t'];

    /// <summary>
    /// Code pages tried when a file is not valid UTF-8. Ordered by likelihood for game rips.
    /// </summary>
    internal static readonly int[] FallbackCodePages = [932, 949, 936, 1251, 866, 1252];

    /// <summary>
    /// Extracts referenced file paths from a CUE sheet file.
    /// </summary>
    /// <param name="cuePath">Path to the CUE file to parse.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A list of file paths referenced by the CUE sheet.</returns>
    internal static Task<List<string>> GetReferencedFilesFromCueAsync(string cuePath, Action<string> onLog,
        CancellationToken token)
    {
        return ParseFileReferenceLinesAsync(cuePath, onLog, "CUE", token);
    }

    /// <summary>
    /// Extracts referenced file paths from a GDI (Dreamcast GD-ROM) file.
    /// </summary>
    /// <param name="gdiPath">Path to the GDI file to parse.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A list of file paths referenced by the GDI file.</returns>
    internal static async Task<List<string>> GetReferencedFilesFromGdiAsync(string gdiPath, Action<string> onLog,
        CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var gdiDir = Path.GetDirectoryName(gdiPath) ?? string.Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(gdiPath, Encoding.UTF8, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            for (var i = 1; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                var firstQuote = trimmedLine.IndexOf('"');
                var lastQuote = trimmedLine.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    var fileName = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    referencedFiles.Add(Path.Combine(gdiDir, fileName));
                }
                else
                {
                    var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5)
                    {
                        continue;
                    }

                    string fileName;
                    if (parts.Length > 6)
                    {
                        var fileNameParts = parts[4..^1];
                        fileName = string.Join(' ', fileNameParts);
                    }
                    else
                    {
                        fileName = parts[4];
                    }

                    referencedFiles.Add(Path.Combine(gdiDir, fileName));
                }
            }
        }
        catch (IOException ex)
        {
            onLog($"[WARNING] Could not parse GDI file: {Path.GetFileName(gdiPath)}. Error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            onLog($"[WARNING] Could not access GDI file: {Path.GetFileName(gdiPath)}. Error: {ex.Message}");
        }

        return referencedFiles;
    }

    /// <summary>
    /// Extracts referenced file paths from a TOC (Table of Contents) file.
    /// </summary>
    /// <param name="tocPath">Path to the TOC file to parse.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A list of file paths referenced by the TOC file.</returns>
    internal static Task<List<string>> GetReferencedFilesFromTocAsync(string tocPath, Action<string> onLog,
        CancellationToken token)
    {
        return ParseFileReferenceLinesAsync(tocPath, onLog, "TOC", token);
    }

    /// <summary>
    /// Reads a CUE/TOC file and returns its lines together with the encoding that was detected
    /// as the most plausible one, and whether the file started with an explicit BOM.
    /// Detection order: BOM, strict UTF-8, then <see cref="FallbackCodePages"/>
    /// filtered to losslessly decodable code pages and scored by how many referenced file names
    /// actually resolve to files in the same directory (ties broken by declared order).
    /// </summary>
    /// <remarks>
    /// The returned <c>HasBom</c> flag matters because chdman's cue parser does not skip a
    /// UTF-8 BOM: the first token becomes "\uFEFFFILE" and the FILE directive is never parsed,
    /// which makes chdman report "couldn't find bin file []" even when every bin exists.
    /// </remarks>
    internal static async Task<(string[] Lines, Encoding Encoding, bool HasBom)> ReadLinesWithDetectedEncodingAsync(
        string filePath, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, token).ConfigureAwait(false);

        switch (bytes.Length)
        {
            // 1) Explicit BOM
            case >= 3 when bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF:
            {
                var bomUtf8 = new UTF8Encoding(false);
                return (DecodeLines(bytes[3..], bomUtf8), bomUtf8, true);
            }
            case >= 4 when bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00:
            {
                // UTF-32LE BOM (FF FE 00 00) — must be checked before the UTF-16LE BOM.
                var bomUtf32 = new UTF32Encoding(false, false);
                return (DecodeLines(bytes[4..], bomUtf32), bomUtf32, true);
            }
            case >= 2 when bytes[0] == 0xFF && bytes[1] == 0xFE:
                return (DecodeLines(bytes[2..], Encoding.Unicode), Encoding.Unicode, true);
            case >= 2 when bytes[0] == 0xFE && bytes[1] == 0xFF:
                return (DecodeLines(bytes[2..], Encoding.BigEndianUnicode), Encoding.BigEndianUnicode, true);
        }

        // 2) Strict UTF-8 (throws on invalid byte sequences)
        try
        {
            return (DecodeLines(bytes, new UTF8Encoding(false, true)), new UTF8Encoding(false), false);
        }
        catch (DecoderFallbackException)
        {
            // not valid UTF-8, try legacy code pages below
        }

        // 3) Legacy code pages, scored by filesystem resolution. A code page is only eligible
        // when it can decode the whole file losslessly (strict decoder); ties are broken by the
        // declared order (most likely first).
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        string[]? onDiskFiles = null;
        if (Directory.Exists(directory))
        {
            onDiskFiles = Directory.GetFiles(directory);
        }

        string[]? bestLines = null;
        Encoding? bestEncoding = null;
        var bestScore = int.MinValue;
        foreach (var codePage in FallbackCodePages)
        {
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (Exception)
            {
                continue;
            }

            string[] decoded;
            try
            {
                decoded = DecodeLines(bytes, encoding);
            }
            catch (DecoderFallbackException)
            {
                // bytes are not representable in this code page
                continue;
            }

            var score = 0;
            if (onDiskFiles is { Length: > 0 })
            {
                foreach (var line in decoded)
                {
                    var trimmedLine = line.Trim();
                    if (TryGetFileNameFromFileLine(trimmedLine, out var fileName) && fileName is not null)
                    {
                        if (onDiskFiles.Any(f =>
                                string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            score += 10;
                        }
                    }
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestEncoding = encoding;
                bestLines = decoded;
            }
        }

        if (bestLines is not null && bestEncoding is not null)
        {
            return (bestLines, bestEncoding, false);
        }

        // 4) Last resort
        return (DecodeLines(bytes, Encoding.Default), Encoding.Default, false);
    }

    /// <summary>
    /// Extracts the referenced file name from a single "FILE ..." line (quoted or unquoted form).
    /// Returns false when the line is not a usable FILE line.
    /// </summary>
    /// <param name="trimmedLine">The trimmed FILE line to parse.</param>
    /// <param name="fileName">The extracted referenced file name, or null when the line is unusable.</param>
    internal static bool TryGetFileNameFromFileLine(string trimmedLine, out string? fileName)
    {
        fileName = null;
        var firstQuote = trimmedLine.IndexOf('"');
        var lastQuote = trimmedLine.LastIndexOf('"');

        if (firstQuote != -1 && lastQuote > firstQuote)
        {
            fileName = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
        }
        else
        {
            var parts = trimmedLine.Split(Separator, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            var rest = parts[1].TrimEnd();
            var lastSpace = rest.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                var afterFilename = rest[(lastSpace + 1)..];
                if (afterFilename.Equals("BINARY", StringComparison.OrdinalIgnoreCase) ||
                    afterFilename.Equals("WAVE", StringComparison.OrdinalIgnoreCase) ||
                    afterFilename.Equals("MP3", StringComparison.OrdinalIgnoreCase) ||
                    afterFilename.Equals("AIFF", StringComparison.OrdinalIgnoreCase) ||
                    afterFilename.Equals("MOTOROLA", StringComparison.OrdinalIgnoreCase) ||
                    afterFilename.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = rest[..lastSpace];
                }
                else
                {
                    fileName = rest;
                }
            }
            else
            {
                fileName = rest;
            }
        }

        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static async Task<List<string>> ParseFileReferenceLinesAsync(
        string filePath, Action<string> onLog, string fileType, CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        try
        {
            var (lines, _, _) = await ReadLinesWithDetectedEncodingAsync(filePath, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryGetFileNameFromFileLine(trimmedLine, out var fileName) || fileName is null)
                {
                    continue;
                }

                referencedFiles.Add(Path.Combine(directory, fileName));
            }
        }
        catch (IOException ex)
        {
            onLog($"[WARNING] Could not parse {fileType} file: {Path.GetFileName(filePath)}. Error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            onLog($"[WARNING] Could not access {fileType} file: {Path.GetFileName(filePath)}. Error: {ex.Message}");
        }

        return referencedFiles;
    }

    private static string[] DecodeLines(byte[] bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}