using System.Text.Json;

namespace Klau.Cli.Import;

/// <summary>
/// Persists and loads column mappings from a .klau-mapping.json file.
/// The file is stored alongside the CSV being imported.
/// </summary>
public static class MappingConfig
{
    public const string FileName = ".klau-mapping.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Resolve the mapping file path for a given CSV directory.
    /// </summary>
    public static string GetPath(string csvDirectory) =>
        Path.Combine(csvDirectory, FileName);

    /// <summary>
    /// Check whether a mapping file exists for the given directory.
    /// </summary>
    public static bool Exists(string csvDirectory) =>
        File.Exists(GetPath(csvDirectory));

    /// <summary>
    /// Load a mapping file from the standard location in a directory.
    /// </summary>
    public static Dictionary<string, string> Load(string csvDirectory) =>
        LoadFromFile(GetPath(csvDirectory));

    /// <summary>
    /// Load a mapping from an explicit file path.
    /// </summary>
    public static Dictionary<string, string> LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Mapping file not found: {filePath}", filePath);

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
    }

    /// <summary>
    /// Save a mapping to the .klau-mapping.json file.
    /// </summary>
    public static void Save(string csvDirectory, Dictionary<string, string> mapping)
    {
        var path = GetPath(csvDirectory);
        var json = JsonSerializer.Serialize(mapping, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Convert a ColumnMapping result into a persistable dictionary.
    /// </summary>
    public static Dictionary<string, string> ToDictionary(ColumnMapping mapping)
    {
        var dict = new Dictionary<string, string>();
        foreach (var match in mapping.Matches)
            dict[match.CsvHeader] = match.KlauField;
        return dict;
    }

    /// <summary>
    /// Convert a persisted dictionary back into a ColumnMapping (all confidence = 1.0 since user-confirmed).
    /// </summary>
    public static ColumnMapping FromDictionary(Dictionary<string, string> dict, IReadOnlyList<string> csvHeaders)
    {
        var matches = new List<ColumnMatch>();
        var unmapped = new List<string>();

        foreach (var header in csvHeaders)
        {
            if (dict.TryGetValue(header, out var field))
                matches.Add(new ColumnMatch(header, field, 1.0));
            else
                unmapped.Add(header);
        }

        return new ColumnMapping(matches, unmapped);
    }
}
