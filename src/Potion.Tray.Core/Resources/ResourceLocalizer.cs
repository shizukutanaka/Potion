using System.Globalization;
using System.Resources;

namespace Potion.Tray.Core.Resources;

public interface ILocalizer
{
    string Get(string key);
    string Format(string key, params object[] args);
}

public sealed class ResourceLocalizer : ILocalizer
{
    private readonly ResourceManager manager = new(
        "Potion.Tray.Core.Resources.Strings",
        typeof(ResourceLocalizer).Assembly);

    public string Get(string key)
    {
        try
        {
            return manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    public string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentUICulture, Get(key), args);
        }
        catch (FormatException)
        {
            return Get(key);
        }
    }
}
