using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Potion.Tray.Core.Resources;
using Xunit;

namespace Potion.Tray.Core.Tests;

public sealed class LocalizationTests
{
    private static readonly string[] Cultures =
        { "ja", "zh-Hans", "ko", "es", "fr", "de", "pt-BR", "ru" };

    [Fact]
    public void SatelliteResourcesHaveTheSameKeysAndPlaceholders()
    {
        var manager = new ResourceManager(
            "Potion.Tray.Core.Resources.Strings",
            typeof(ResourceLocalizer).Assembly);
        var neutral = Keys(manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!);
        foreach (var cultureName in Cultures)
        {
            var set = manager.GetResourceSet(CultureInfo.GetCultureInfo(cultureName), true, false);
            Assert.NotNull(set);
            var localized = Keys(set!);
            Assert.Equal(neutral.OrderBy(k => k), localized.OrderBy(k => k));
            foreach (var key in neutral)
            {
                Assert.Equal(
                    Placeholders(manager.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty),
                    Placeholders(manager.GetString(key, CultureInfo.GetCultureInfo(cultureName)) ?? string.Empty));
            }
        }
    }

    [Fact]
    public void UnknownKeyReturnsTheKey()
    {
        var localizer = new ResourceLocalizer();
        Assert.Equal("Unknown.Key", localizer.Get("Unknown.Key"));
    }

    [Fact]
    public void CurrentUiCultureSelectsTranslationAndFallsBackToNeutral()
    {
        var localizer = new ResourceLocalizer();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
            Assert.Equal("設定", localizer.Get("Ui.Settings.Title"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            Assert.Equal("Einstellungen", localizer.Get("Ui.Settings.Title"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sv");
            Assert.Equal("Settings", localizer.Get("Ui.Settings.Title"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static HashSet<string> Keys(ResourceSet set) =>
        set.Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{(\d+)(?:,[^}]*)?(?:\:[^}]*)?\}")
            .Select(match => match.Groups[1].Value)
            .OrderBy(index => index)
            .ToArray();
}
