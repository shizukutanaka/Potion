using System.Diagnostics;

namespace Potion.Tray.Core;

public static class CommandAllowList
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "sfc.exe", "DISM.exe", "sc.exe", "ipconfig.exe", "cleanmgr.exe"
    };

    public static bool IsAllowed(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '&' }) >= 0)
        {
            return false;
        }

        return Allowed.Contains(fileName);
    }
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!CommandAllowList.IsAllowed(fileName))
        {
            throw new InvalidOperationException($"許可されていないコマンドです: {fileName}");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var started = Stopwatch.GetTimestamp();
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        var waitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout, ct));
        if (completed != waitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await Task.WhenAll(outputTask, errorTask);
            return new ProcessRunResult(
                -1,
                OutputTail(outputTask.Result),
                OutputTail(errorTask.Result),
                Stopwatch.GetElapsedTime(started),
                TimedOut: true);
        }

        await waitTask;
        return new ProcessRunResult(
            process.ExitCode,
            OutputTail(await outputTask),
            OutputTail(await errorTask),
            Stopwatch.GetElapsedTime(started),
            TimedOut: false);
    }

    public static string OutputTail(string output, int maxLength = 4000) =>
        string.IsNullOrEmpty(output) || output.Length <= maxLength
            ? output
            : output[^maxLength..];
}
