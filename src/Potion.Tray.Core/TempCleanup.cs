using System.Diagnostics;

namespace Potion.Tray.Core;

public sealed class TempDirectoryCleaner
{
    private static readonly TimeSpan DefaultMaxDurationPerRoot = TimeSpan.FromSeconds(30);
    private readonly ITrayClock clock;
    private readonly int maxFilesPerRoot;
    private readonly TimeSpan maxDurationPerRoot;

    public TempDirectoryCleaner(
        ITrayClock clock,
        int maxFilesPerRoot = 50_000,
        TimeSpan? maxDurationPerRoot = null)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.maxFilesPerRoot = Math.Max(1, maxFilesPerRoot);
        this.maxDurationPerRoot = maxDurationPerRoot is null
            ? DefaultMaxDurationPerRoot
            : maxDurationPerRoot.Value <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : maxDurationPerRoot.Value;
    }

    public TempCleanupResult Clean(
        IEnumerable<string> roots,
        TimeSpan minimumAge,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var cutoff = clock.UtcNow.UtcDateTime - minimumAge;
        var filesDeleted = 0;
        long bytesFreed = 0;

        foreach (var root in roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            DirectoryInfo rootInfo;
            try
            {
                rootInfo = new DirectoryInfo(root);
                if (!rootInfo.Exists ||
                    rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var examined = 0;
            var directories = new Stack<DirectoryInfo>();
            directories.Push(rootInfo);

            while (directories.Count > 0 &&
                   examined < maxFilesPerRoot &&
                   stopwatch.Elapsed < maxDurationPerRoot &&
                   !ct.IsCancellationRequested)
            {
                var directory = directories.Pop();
                IEnumerator<FileSystemInfo> entries;
                try
                {
                    entries = directory.EnumerateFileSystemInfos().GetEnumerator();
                }
                catch
                {
                    continue;
                }

                using (entries)
                {
                    while (examined < maxFilesPerRoot &&
                           stopwatch.Elapsed < maxDurationPerRoot &&
                           !ct.IsCancellationRequested)
                    {
                        FileSystemInfo entry;
                        try
                        {
                            if (!entries.MoveNext())
                            {
                                break;
                            }

                            entry = entries.Current;
                        }
                        catch
                        {
                            break;
                        }

                        try
                        {
                            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                continue;
                            }

                            if (entry is DirectoryInfo childDirectory)
                            {
                                directories.Push(childDirectory);
                                continue;
                            }

                            if (entry is not FileInfo file)
                            {
                                continue;
                            }

                            examined++;
                            if (file.LastWriteTimeUtc < cutoff)
                            {
                                var size = file.Length;
                                file.Delete();
                                filesDeleted++;
                                bytesFreed += size;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        return new TempCleanupResult(filesDeleted, bytesFreed);
    }
}
