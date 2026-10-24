using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace GitHubTools;

/// <summary>
/// The source code of a repository at one commit, extracted from GitHub's tarball into a temporary
/// directory. Provides the read-only operations the triage agent uses as tools; all of them only
/// read below <see cref="Root"/>. Dispose deletes the directory.
/// </summary>
public sealed class RepositorySnapshot : IDisposable
{
    private const long MaxFileSize = 1_000_000;
    private const long MaxTotalSize = 300_000_000;
    private const int MaxFileCount = 20_000;
    private const int MaxListedFiles = 500;
    private const int MaxReadLines = 400;
    private const int MaxReadChars = 40_000;
    private const int MaxSearchMatches = 50;
    private const int MaxMatchLineLength = 200;
    private const int MaxPathLength = 1_000;

    private readonly string _rootWithSeparator;
    private int _skippedLargeFiles;
    private int _skippedUnsafeEntries;
    private bool _truncated;

    private RepositorySnapshot(string root)
    {
        Root = Path.GetFullPath(root);
        _rootWithSeparator = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
    }

    public string Root { get; }

    /// <summary>
    /// Extracts a GitHub tarball (.tar.gz) into <paramref name="root"/>. Only regular files are
    /// extracted: symbolic and hard links are skipped, so no path below the root can point outside it.
    /// GitHub's top-level directory (&lt;owner&gt;-&lt;repo&gt;-&lt;sha&gt;/) is removed. Files over 1 MB are
    /// skipped and extraction stops at 300 MB or 20,000 files; the snapshot then reports itself as incomplete.
    /// </summary>
    public static async Task<RepositorySnapshot> ExtractAsync(Stream tarGz, string root, CancellationToken cancellationToken = default)
    {
        var snapshot = new RepositorySnapshot(root);
        Directory.CreateDirectory(snapshot.Root);

        try
        {
            await using var gzip = new GZipStream(tarGz, CompressionMode.Decompress);
            await using var reader = new TarReader(gzip);

            long totalSize = 0;
            var fileCount = 0;
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    {
                        snapshot._skippedUnsafeEntries++;
                    }

                    continue;
                }

                var slash = entry.Name.IndexOf('/');
                var relativePath = slash < 0 ? "" : entry.Name[(slash + 1)..];
                if (!IsSafeRelativePath(relativePath))
                {
                    snapshot._skippedUnsafeEntries++;
                    continue;
                }

                if (entry.Length > MaxFileSize)
                {
                    snapshot._skippedLargeFiles++;
                    continue;
                }

                if (fileCount >= MaxFileCount || totalSize + entry.Length > MaxTotalSize)
                {
                    snapshot._truncated = true;
                    break;
                }

                var fullPath = Path.GetFullPath(Path.Combine(snapshot.Root, relativePath));
                if (!fullPath.StartsWith(snapshot._rootWithSeparator, StringComparison.Ordinal) || File.Exists(fullPath))
                {
                    snapshot._skippedUnsafeEntries++;
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await entry.ExtractToFileAsync(fullPath, overwrite: false, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // E.g. a file and a directory with the same name (case-insensitive file systems).
                    snapshot._skippedUnsafeEntries++;
                    continue;
                }

                totalSize += entry.Length;
                fileCount++;
            }

            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    /// <summary>Lists the files (path and size), optionally only below a directory.</summary>
    public string ListFiles(string? directory = null)
    {
        var start = string.IsNullOrWhiteSpace(directory) ? Root : Resolve(directory);
        if (!Directory.Exists(start))
        {
            return File.Exists(start)
                ? $"'{directory}' is a file. Use read_file to read it."
                : $"Directory '{directory}' does not exist.";
        }

        var files = Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
            .Select(path => (Path: ToRelativePath(path), Size: new FileInfo(path).Length))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        foreach (var (path, size) in files.Take(MaxListedFiles))
        {
            sb.AppendLine($"{path} ({size} bytes)");
        }

        if (files.Count == 0)
        {
            sb.AppendLine("No files.");
        }
        else if (files.Count > MaxListedFiles)
        {
            sb.AppendLine($"…{files.Count - MaxListedFiles} more files. List a subdirectory to see them.");
        }

        AppendCompletenessNote(sb);
        return sb.ToString();
    }

    /// <summary>Reads a text file with line numbers, from <paramref name="startLine"/> on.</summary>
    public string ReadFile(string path, int startLine = 1)
    {
        var fullPath = Resolve(path);
        if (!File.Exists(fullPath))
        {
            return $"File '{path}' does not exist (or was skipped because it is larger than 1 MB). Use list_files to find the correct path.";
        }

        var content = File.ReadAllBytes(fullPath);
        if (Array.IndexOf(content, (byte)0) >= 0)
        {
            return $"'{path}' is a binary file ({content.Length} bytes).";
        }

        startLine = Math.Max(1, startLine);
        var lines = Encoding.UTF8.GetString(content).Split('\n');
        if (startLine > lines.Length)
        {
            return $"'{path}' has only {lines.Length} lines.";
        }

        var sb = new StringBuilder();
        var line = startLine;
        for (; line <= lines.Length && line < startLine + MaxReadLines && sb.Length < MaxReadChars; line++)
        {
            sb.Append(line).Append(": ").AppendLine(lines[line - 1].TrimEnd('\r'));
        }

        if (line <= lines.Length)
        {
            sb.AppendLine($"…(file has {lines.Length} lines; call read_file with startLine {line} to continue)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Case-insensitive text search over all text files, optionally only below a directory.
    /// Returns the matching lines as "path:line: text".
    /// </summary>
    public string SearchCode(string query, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return "The search text must have at least 2 characters.";
        }

        query = query.Trim();
        var start = string.IsNullOrWhiteSpace(directory) ? Root : Resolve(directory);
        if (!Directory.Exists(start))
        {
            return $"Directory '{directory}' does not exist.";
        }

        var files = Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in files)
        {
            var content = File.ReadAllBytes(file);
            if (Array.IndexOf(content, (byte)0) >= 0)
            {
                continue;
            }

            var lines = Encoding.UTF8.GetString(content).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (++matches > MaxSearchMatches)
                {
                    sb.AppendLine($"…more than {MaxSearchMatches} matches. Use a more specific search text or a directory.");
                    AppendCompletenessNote(sb);
                    return sb.ToString();
                }

                var text = lines[i].Trim();
                if (text.Length > MaxMatchLineLength)
                {
                    text = text[..MaxMatchLineLength] + "…";
                }

                sb.AppendLine($"{ToRelativePath(file)}:{i + 1}: {text}");
            }
        }

        if (matches == 0)
        {
            sb.AppendLine($"No matches for '{query}'.");
        }

        AppendCompletenessNote(sb);
        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // Resolves a path from the model to a full path below the root.
    private string Resolve(string path)
    {
        path = path.Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0)
        {
            return Root;
        }

        if (!IsSafeRelativePath(path))
        {
            throw new GitHubIssueException("Paths must be relative to the repository root and must not contain '.' or '..' segments.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(Root, path));
        return fullPath.StartsWith(_rootWithSeparator, StringComparison.Ordinal)
            ? fullPath
            : throw new GitHubIssueException($"'{path}' is outside the repository.");
    }

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= MaxPathLength &&
        !path.Contains('\\') && !path.Contains('\0') && !path.Contains(':') &&
        path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private void AppendCompletenessNote(StringBuilder sb)
    {
        if (_truncated)
        {
            sb.AppendLine($"(The repository is larger than the snapshot limit of {MaxFileCount} files / {MaxTotalSize / 1_000_000} MB; some files are missing.)");
        }

        if (_skippedLargeFiles > 0)
        {
            sb.AppendLine($"({_skippedLargeFiles} files larger than 1 MB are not included.)");
        }

        if (_skippedUnsafeEntries > 0)
        {
            sb.AppendLine($"({_skippedUnsafeEntries} links or entries with unsafe paths are not included.)");
        }
    }
}
