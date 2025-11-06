using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

public class TranslationManager
{
    private readonly string _resourcesPath;

    public TranslationManager(string resourcesPath)
    {
        _resourcesPath = resourcesPath;
    }

    public void ValidateAllTranslations()
    {
        Console.WriteLine("🔍 Validating translations for all languages...");

        var masterKeys = GetTranslationKeys("ControllerStrings.resx");
        Console.WriteLine($"📊 Found {masterKeys.Count} translation keys in master file");

        var supportedLanguages = new[] { "en", "ja", "es", "fr", "de", "ko", "zh", "ru", "ar", "hi", "bn", "ur", "id", "it", "nl", "pt", "vi", "th" };

        foreach (var language in supportedLanguages)
        {
            var languageFile = $"ControllerStrings.{language}.resx";
            var filePath = Path.Combine(_resourcesPath, languageFile);

            if (File.Exists(filePath))
            {
                var languageKeys = GetTranslationKeys(languageFile);
                var missing = masterKeys.Except(languageKeys).ToList();

                if (missing.Any())
                {
                    Console.WriteLine($"❌ {language}: Missing {missing.Count} translations");
                    foreach (var key in missing.Take(5)) // Show first 5 missing keys
                    {
                        Console.WriteLine($"   - {key}");
                    }
                    if (missing.Count > 5)
                    {
                        Console.WriteLine($"   ... and {missing.Count - 5} more");
                    }
                }
                else
                {
                    Console.WriteLine($"✅ {language}: All translations complete");
                }
            }
            else
            {
                Console.WriteLine($"❌ {language}: Resource file not found at {filePath}");
            }
        }
    }

    public void ValidateHealthControllerTranslations()
    {
        Console.WriteLine("🏥 Validating HealthController translations...");

        var masterKeys = GetTranslationKeys("HealthController.en.resx");
        Console.WriteLine($"📊 Found {masterKeys.Count} HealthController translation keys in master file");

        var supportedLanguages = new[] { "en", "ja", "es", "fr", "de", "ko", "zh", "ru", "ar", "hi", "bn", "ur", "id", "it", "nl", "pt", "vi", "th" };

        foreach (var language in supportedLanguages)
        {
            var languageFile = $"HealthController.{language}.resx";
            var filePath = Path.Combine(_resourcesPath, languageFile);

            if (File.Exists(filePath))
            {
                var languageKeys = GetTranslationKeys(languageFile);
                var missing = masterKeys.Except(languageKeys).ToList();

                if (missing.Any())
                {
                    Console.WriteLine($"❌ HealthController.{language}: Missing {missing.Count} translations");
                    foreach (var key in missing.Take(3))
                    {
                        Console.WriteLine($"   - {key}");
                    }
                }
                else
                {
                    Console.WriteLine($"✅ HealthController.{language}: All translations complete");
                }
            }
            else
            {
                Console.WriteLine($"❌ HealthController.{language}: Resource file not found");
            }
        }
    }

    public void ShowSummary()
    {
        Console.WriteLine("\n📈 Translation Summary:");
        Console.WriteLine("========================");

        var supportedLanguages = new[] { "en", "ja", "es", "fr", "de", "ko", "zh", "ru", "ar", "hi", "bn", "ur", "id", "it", "nl", "pt", "vi", "th" };

        Console.WriteLine($"Total supported languages: {supportedLanguages.Length}");
        Console.WriteLine($"ControllerStrings files: {supportedLanguages.Count(lang => File.Exists(Path.Combine(_resourcesPath, $"ControllerStrings.{lang}.resx")))}");
        Console.WriteLine($"HealthController files: {supportedLanguages.Count(lang => File.Exists(Path.Combine(_resourcesPath, $"HealthController.{lang}.resx")))}");

        var masterKeys = GetTranslationKeys("ControllerStrings.resx");
        var healthMasterKeys = GetTranslationKeys("HealthController.en.resx");

        Console.WriteLine($"ControllerStrings keys: {masterKeys.Count}");
        Console.WriteLine($"HealthController keys: {healthMasterKeys.Count}");
    }

    private HashSet<string> GetTranslationKeys(string fileName)
    {
        var filePath = Path.Combine(_resourcesPath, fileName);
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Warning: File not found: {filePath}");
            return new HashSet<string>();
        }

        try
        {
            var doc = XDocument.Load(filePath);
            var keys = doc.Descendants("data")
                         .Select(d => d.Attribute("name")?.Value)
                         .Where(k => k != null)
                         .ToHashSet();

            return keys ?? new HashSet<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading {fileName}: {ex.Message}");
            return new HashSet<string>();
        }
    }
}

class Program
{
    static void Main(string[] args)
    {
        var resourcesPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "Potion.Service", "Resources");

        if (!Directory.Exists(resourcesPath))
        {
            Console.WriteLine($"❌ Resources directory not found: {resourcesPath}");
            return;
        }

        var manager = new TranslationManager(resourcesPath);

        Console.WriteLine("🔧 Potion Translation Manager");
        Console.WriteLine("============================");

        if (args.Length == 0 || args.Contains("validate"))
        {
            manager.ValidateAllTranslations();
            manager.ValidateHealthControllerTranslations();
        }

        if (args.Contains("summary") || args.Length == 0)
        {
            manager.ShowSummary();
        }

        if (args.Contains("help"))
        {
            Console.WriteLine("\nUsage:");
            Console.WriteLine("  TranslationManager.exe [command]");
            Console.WriteLine("Commands:");
            Console.WriteLine("  validate  - Validate all translations (default)");
            Console.WriteLine("  summary   - Show translation summary");
            Console.WriteLine("  help      - Show this help");
        }

        Console.WriteLine("\n✨ Translation management completed!");
    }
}
