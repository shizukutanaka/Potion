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

public static class HistoryText
{
    public static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    public static string CommandSummary(IReadOnlyList<CommandExecution> commands) =>
        string.Join(
            " ; ",
            commands.Select(command =>
                string.IsNullOrWhiteSpace(command.Arguments)
                    ? $"{command.FileName} -> {command.ExitCode}"
                    : $"{command.FileName} {command.Arguments} -> {command.ExitCode}"));
}

public static class HistorySearch
{
    public static bool Matches(HistoryEntry entry, string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        var search = term.Trim();
        return ContainsLocalized(entry.Title) ||
            ContainsLocalized(entry.Detail) ||
            ContainsLocalized(entry.RepairSummary) ||
            ContainsLocalized(entry.SkipReason) ||
            ContainsTechnical(entry.CheckId) ||
            entry.Commands.Any(command =>
                ContainsTechnical(command.FileName) ||
                ContainsTechnical(command.Arguments) ||
                ContainsTechnical(command.ExitCode.ToString(CultureInfo.InvariantCulture)));

        bool ContainsLocalized(string? value) =>
            value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

        bool ContainsTechnical(string? value) =>
            value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }
}

public static class DurationFormatter
{
    public static string Format(TimeSpan value, ILocalizer localizer)
    {
        var seconds = Math.Max(0, value.TotalSeconds);
        return seconds < 60
            ? localizer.Format("Format.Seconds", seconds.ToString("0.0", CultureInfo.CurrentUICulture))
            : localizer.Format(
                "Format.Minutes",
                (seconds / 60).ToString("0.0", CultureInfo.CurrentUICulture));
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
