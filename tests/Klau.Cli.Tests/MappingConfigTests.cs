using Klau.Cli.Import;
using Xunit;

namespace Klau.Cli.Tests;

public class MappingConfigTests
{
    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"klau-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var original = new Dictionary<string, string>
            {
                ["Customer Name"] = "CustomerName",
                ["Street"] = "SiteAddress",
                ["Order #"] = "ExternalId"
            };

            MappingConfig.Save(dir, original);
            Assert.True(MappingConfig.Exists(dir));

            var loaded = MappingConfig.Load(dir);
            Assert.Equal(3, loaded.Count);
            Assert.Equal("CustomerName", loaded["Customer Name"]);
            Assert.Equal("SiteAddress", loaded["Street"]);
            Assert.Equal("ExternalId", loaded["Order #"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadFromFile_ExplicitPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klau-test-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{"Col1":"CustomerName","Col2":"SiteAddress"}""");
            var dict = MappingConfig.LoadFromFile(path);

            Assert.Equal(2, dict.Count);
            Assert.Equal("CustomerName", dict["Col1"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFromFile_ThrowsOnMissing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            MappingConfig.LoadFromFile("/nonexistent/path.json"));
    }

    [Fact]
    public void FromDictionary_MapsMatchingHeaders()
    {
        var dict = new Dictionary<string, string>
        {
            ["Customer"] = "CustomerName",
            ["City"] = "SiteCity"
        };
        IReadOnlyList<string> headers = ["Customer", "City", "Extra"];

        var mapping = MappingConfig.FromDictionary(dict, headers);

        Assert.Equal(2, mapping.Matches.Count);
        Assert.Single(mapping.UnmappedHeaders);
        Assert.Equal("Extra", mapping.UnmappedHeaders[0]);
    }

    [Fact]
    public void ToDictionary_ConvertsMapping()
    {
        var mapping = new ColumnMapping(
            [new ColumnMatch("A", "CustomerName", 1.0), new ColumnMatch("B", "SiteCity", 0.8)],
            ["C"]);

        var dict = MappingConfig.ToDictionary(mapping);

        Assert.Equal(2, dict.Count);
        Assert.Equal("CustomerName", dict["A"]);
        Assert.Equal("SiteCity", dict["B"]);
    }
}
