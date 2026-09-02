namespace Potion.Tray.Core;

public static class StartupRegistrationPolicy
{
    public static bool Matches(string? registeredCommand, string executablePath)
    {
        var registered = Normalize(registeredCommand);
        var executable = Normalize(executablePath);
        return registered.Length > 0 &&
               executable.Length > 0 &&
               StringComparer.OrdinalIgnoreCase.Equals(registered, executable);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.Length >= 2 &&
            normalized[0] == '"' &&
            normalized[^1] == '"')
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }
}
