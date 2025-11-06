#!/usr/bin/env dotnet-script

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public class TranslationManager
{
    private readonly string _resourcesPath;
    private readonly HashSet<string> _supportedLanguages;

    public TranslationManager(string resourcesPath)
    {
        _resourcesPath = resourcesPath;
        _supportedLanguages = new HashSet<string> { "en", "ja", "es", "fr", "de", "ko", "zh", "ru", "ar", "hi", "bn", "ur", "id", "it", "nl", "pt" };
    }

    public void ValidateAllTranslations()
    {
        Console.WriteLine("🔍 Validating translations for all languages...");

        var masterKeys = GetTranslationKeys("ControllerStrings.resx");
        var missingTranslations = new Dictionary<string, List<string>>();

        foreach (var language in _supportedLanguages)
        {
            var languageFile = $"ControllerStrings.{language}.resx";
            if (File.Exists(Path.Combine(_resourcesPath, languageFile)))
            {
                var languageKeys = GetTranslationKeys(languageFile);
                var missing = masterKeys.Except(languageKeys).ToList();

                if (missing.Any())
                {
                    missingTranslations[language] = missing;
                    Console.WriteLine($"❌ {language}: Missing {missing.Count} translations");
                    foreach (var key in missing)
                    {
                        Console.WriteLine($"   - {key}");
                    }
                }
                else
                {
                    Console.WriteLine($"✅ {language}: All translations complete");
                }
            }
            else
            {
                Console.WriteLine($"❌ {language}: Resource file not found");
            }
        }

        if (!missingTranslations.Any())
        {
            Console.WriteLine("🎉 All languages have complete translations!");
        }
    }

    public void GenerateMissingTranslations()
    {
        Console.WriteLine("🔧 Generating missing translations...");

        var masterKeys = GetTranslationKeys("ControllerStrings.resx");

        foreach (var language in _supportedLanguages.Where(lang => lang != "en"))
        {
            var languageFile = $"ControllerStrings.{language}.resx";
            var filePath = Path.Combine(_resourcesPath, languageFile);

            if (File.Exists(filePath))
            {
                var existingKeys = GetTranslationKeys(languageFile);
                var missingKeys = masterKeys.Except(existingKeys).ToList();

                if (missingKeys.Any())
                {
                    AddMissingTranslations(filePath, missingKeys);
                    Console.WriteLine($"📝 Added {missingKeys.Count} missing translations to {language}");
                }
            }
            else
            {
                CreateNewLanguageFile(language, masterKeys);
                Console.WriteLine($"✨ Created new resource file for {language}");
            }
        }
    }

    public void ValidateHealthControllerTranslations()
    {
        Console.WriteLine("🏥 Validating HealthController translations...");

        var masterKeys = GetTranslationKeys("HealthController.en.resx");
        var missingTranslations = new Dictionary<string, List<string>>();

        foreach (var language in _supportedLanguages)
        {
            var languageFile = $"HealthController.{language}.resx";
            if (File.Exists(Path.Combine(_resourcesPath, languageFile)))
            {
                var languageKeys = GetTranslationKeys(languageFile);
                var missing = masterKeys.Except(languageKeys).ToList();

                if (missing.Any())
                {
                    missingTranslations[language] = missing;
                    Console.WriteLine($"❌ HealthController.{language}: Missing {missing.Count} translations");
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

    private HashSet<string> GetTranslationKeys(string fileName)
    {
        var filePath = Path.Combine(_resourcesPath, fileName);
        if (!File.Exists(filePath))
            return new HashSet<string>();

        var doc = XDocument.Load(filePath);
        var keys = doc.Descendants("data")
                     .Select(d => d.Attribute("name")?.Value)
                     .Where(k => k != null)
                     .ToHashSet();

        return keys!;
    }

    private void AddMissingTranslations(string filePath, List<string> missingKeys)
    {
        var doc = XDocument.Load(filePath);
        var lastDataElement = doc.Descendants("data").LastOrDefault();

        foreach (var key in missingKeys)
        {
            var newData = new XElement("data",
                new XAttribute("name", key),
                new XAttribute("xml:space", "preserve"),
                new XElement("value", $"[TRANSLATE] {key}"));

            if (lastDataElement != null)
            {
                lastDataElement.AddAfterSelf(newData);
            }
            else
            {
                doc.Root?.Add(newData);
            }
            lastDataElement = newData;
        }

        doc.Save(filePath);
    }

    private void CreateNewLanguageFile(string language, HashSet<string> masterKeys)
    {
        var template = @"<?xml version=""1.0"" encoding=""utf-8""?>
<root>
  <xsd:schema id=""root"" xmlns="""" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:msdata=""urn:schemas-microsoft-com:xml-msdata"">
    <xsd:import namespace=""http://www.w3.org/XML/1998/namespace"" />
    <xsd:element name=""root"" msdata:IsDataSet=""true"">
      <xsd:complexType>
        <xsd:choice maxOccurs=""unbounded"">
          <xsd:element name=""metadata"">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name=""value"" type=""xsd:string"" minOccurs=""0"" />
              </xsd:sequence>
              <xsd:attribute name=""name"" use=""required"" type=""xsd:string"" />
              <xsd:attribute name=""type"" type=""xsd:string"" />
              <xsd:attribute name=""mimetype"" type=""xsd:string"" />
              <xsd:attribute ref=""xml:space"" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name=""assembly"">
            <xsd:complexType>
              <xsd:attribute name=""alias"" type=""xsd:string"" />
              <xsd:attribute name=""name"" type=""xsd:string"" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name=""data"">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name=""value"" type=""xsd:string"" minOccurs=""0"" msdata:Ordinal=""1"" />
                <xsd:element name=""comment"" type=""xsd:string"" minOccurs=""0"" msdata:Ordinal=""2"" />
              </xsd:sequence>
              <xsd:attribute name=""name"" type=""xsd:string"" use=""required"" msdata:Ordinal=""1"" />
              <xsd:attribute name=""type"" type=""xsd:string"" msdata:Ordinal=""3"" />
              <xsd:attribute name=""mimetype"" type=""xsd:string"" msdata:Ordinal=""4"" />
              <xsd:attribute ref=""xml:space"" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name=""resheader"">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name=""value"" type=""xsd:string"" minOccurs=""0"" msdata:Ordinal=""1"" />
              </xsd:sequence>
              <xsd:attribute name=""name"" type=""xsd:string"" use=""required"" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name=""resmimetype"">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name=""version"">
    <value>2.0</value>
  </resheader>
  <resheader name=""reader"">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name=""writer"">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
{0}
</root>";

        var dataElements = string.Join(Environment.NewLine,
            masterKeys.Select(key => $"  <data name=\"{key}\" xml:space=\"preserve\">{Environment.NewLine}    <value>[TRANSLATE] {key}</value>{Environment.NewLine}  </data>"));

        var content = string.Format(template, dataElements);
        var filePath = Path.Combine(_resourcesPath, $"ControllerStrings.{language}.resx");
        File.WriteAllText(filePath, content);
    }
}

// Main execution
var resourcesPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "Potion.Service", "Resources");
var manager = new TranslationManager(resourcesPath);

// Parse command line arguments
var args = Environment.GetCommandLineArgs();
if (args.Length > 1)
{
    switch (args[1])
    {
        case "validate":
            manager.ValidateAllTranslations();
            manager.ValidateHealthControllerTranslations();
            break;
        case "generate":
            manager.GenerateMissingTranslations();
            break;
        case "validate-health":
            manager.ValidateHealthControllerTranslations();
            break;
        default:
            Console.WriteLine("Usage: TranslationManager [validate|generate|validate-health]");
            break;
    }
}
else
{
    Console.WriteLine("🔧 Potion Translation Manager");
    Console.WriteLine("Usage: dotnet script TranslationManager.csx [command]");
    Console.WriteLine("Commands:");
    Console.WriteLine("  validate        - Validate all translations");
    Console.WriteLine("  generate        - Generate missing translations");
    Console.WriteLine("  validate-health - Validate HealthController translations only");
}
