namespace Potion.Tray.Core;

public static class DriveScope
{
    public static string Root(string? path)
    {
        var value = path?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        {
            return $"{char.ToUpperInvariant(value[0])}:\\";
        }

        return Path.GetPathRoot(value) ?? string.Empty;
    }

    public static bool Includes(IReadOnlyCollection<string>? affectedRoots, string? path)
    {
        if (affectedRoots is null || affectedRoots.Count == 0)
        {
            return true;
        }

        var root = Normalize(Root(path));
        return root.Length > 0 &&
            affectedRoots.Any(affectedRoot =>
                string.Equals(root, Normalize(Root(affectedRoot)), StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string root) =>
        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/');
}
