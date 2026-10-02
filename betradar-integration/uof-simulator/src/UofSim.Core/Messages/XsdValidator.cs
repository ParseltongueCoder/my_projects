using System.Xml;
using System.Xml.Schema;

namespace UofSim.Core.Messages;

/// <summary>
/// Validates XML against the official Sportradar XSDs loaded from a developer-supplied directory
/// (<c>UOF_XSD_DIR</c>). The XSDs are never stored in this repository (SDK License Agreement).
/// </summary>
public sealed class XsdValidator
{
    public const string FeedSchema = "UnifiedFeed.xsd";
    public const string DescriptionsSchema = "UnifiedFeedDescriptions.xsd";

    private readonly XmlSchemaSet _schemas;

    private XsdValidator(XmlSchemaSet schemas) => _schemas = schemas;

    /// <summary>Returns null when the directory or the schema file is missing - validation is then skipped.</summary>
    public static XsdValidator? TryLoad(string? xsdDir, string schemaFile)
    {
        if (string.IsNullOrWhiteSpace(xsdDir))
        {
            return null;
        }
        var path = Path.Combine(xsdDir, schemaFile);
        if (!File.Exists(path))
        {
            return null;
        }
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.Add(null, path);
        set.Compile();
        return new XsdValidator(set);
    }

    /// <summary>Returns the validation errors; an empty list means the document is valid.</summary>
    public IReadOnlyList<string> Validate(byte[] xml)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = _schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity}: {e.Message}");
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read())
        {
        }
        return errors;
    }
}
