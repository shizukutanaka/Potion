using System.Globalization;
using Potion.Tray.Core.Resources;

namespace Potion.Tray.Core;

internal static class ByteFormatter
{
    public static string Gigabytes(long bytes, ILocalizer localizer)
    {
        var value = (bytes / (1024d * 1024d * 1024d)).ToString("0.0", CultureInfo.CurrentUICulture);
        return localizer.Format("Format.Gigabytes", value);
    }
}

internal static class CommandExecutionFactory
{
    public static CommandExecution Create(
        string fileName,
        IReadOnlyList<string> arguments,
        ProcessRunResult result) =>
        new(
            fileName,
            string.Join(" ", arguments),
            result.ExitCode,
            result.Duration,
            result.StandardOutput,
            result.StandardError);
}
