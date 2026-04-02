using Klau.Cli.Domain;
using Klau.Cli.Import;
using Xunit;

namespace Klau.Cli.Tests;

public class JobImportRowTests
{
    private static SpreadsheetData MakeData(string[] headers, params string[][] rows) =>
        new(headers, rows.Select(r => (IReadOnlyList<string>)r).ToList(), "CSV");

    [Fact]
    public void MapRows_MissingCustomerName_SkipsRowWithWarning()
    {
        var data = MakeData(
            ["Address", "City"],
            ["123 Main St", "Springfield"]);

        var mapping = new ColumnMapping(
            [new ColumnMatch("Address", "SiteAddress", 1.0), new ColumnMatch("City", "SiteCity", 1.0)],
            []);

        var result = ImportPipeline.MapRows(data, mapping, "2026-04-03");

        Assert.Empty(result.Rows);
        Assert.Single(result.Warnings);
        Assert.Contains("missing customer name", result.Warnings[0].Message);
    }

    [Fact]
    public void MapRows_NoRequestedDate_DefaultsToDispatchDate()
    {
        var data = MakeData(
            ["Customer"],
            ["Acme Corp"]);

        var mapping = new ColumnMapping(
            [new ColumnMatch("Customer", "CustomerName", 1.0)],
            []);

        var result = ImportPipeline.MapRows(data, mapping, "2026-04-03");

        Assert.Single(result.Rows);
        Assert.Equal("2026-04-03", result.Rows[0].RequestedDate);
    }

    [Fact]
    public void MapRows_WithRequestedDate_UsesRowDate()
    {
        var data = MakeData(
            ["Customer", "Date"],
            ["Acme Corp", "2026-05-01"]);

        var mapping = new ColumnMapping(
            [new ColumnMatch("Customer", "CustomerName", 1.0),
             new ColumnMatch("Date", "RequestedDate", 1.0)],
            []);

        var result = ImportPipeline.MapRows(data, mapping, "2026-04-03");

        Assert.Single(result.Rows);
        Assert.Equal("2026-05-01", result.Rows[0].RequestedDate);
    }

    [Fact]
    public void MapRows_ShortRow_HandlesGracefully()
    {
        var data = MakeData(
            ["Customer", "Address", "City"],
            ["Acme Corp"]); // only 1 column but 3 headers

        var mapping = new ColumnMapping(
            [new ColumnMatch("Customer", "CustomerName", 1.0),
             new ColumnMatch("Address", "SiteAddress", 1.0),
             new ColumnMatch("City", "SiteCity", 1.0)],
            []);

        var result = ImportPipeline.MapRows(data, mapping, "2026-04-03");

        Assert.Single(result.Rows);
        Assert.Equal("Acme Corp", result.Rows[0].CustomerName);
        Assert.Null(result.Rows[0].SiteAddress);
    }

    [Fact]
    public void MapRows_WhitespaceValues_Trimmed()
    {
        var data = MakeData(
            ["Customer", "City"],
            ["  Acme Corp  ", "  Springfield  "]);

        var mapping = new ColumnMapping(
            [new ColumnMatch("Customer", "CustomerName", 1.0),
             new ColumnMatch("City", "SiteCity", 1.0)],
            []);

        var result = ImportPipeline.MapRows(data, mapping, "2026-04-03");

        Assert.Equal("Acme Corp", result.Rows[0].CustomerName);
        Assert.Equal("Springfield", result.Rows[0].SiteCity);
    }

    [Fact]
    public void MapRows_AllFieldsMapped()
    {
        var data = MakeData(
            ["Customer", "Site", "Address", "City", "State", "Zip", "Type", "Size", "Window", "Priority", "Notes", "Date", "OrderNo"],
            ["Acme", "Main", "123 St", "City", "CA", "90210", "DELIVERY", "30", "MORNING", "HIGH", "Gate code", "2026-04-03", "WO-1"]);

        var mapping = new ColumnMapping(
            [new("Customer", "CustomerName", 1.0), new("Site", "SiteName", 1.0),
             new("Address", "SiteAddress", 1.0), new("City", "SiteCity", 1.0),
             new("State", "SiteState", 1.0), new("Zip", "SiteZip", 1.0),
             new("Type", "JobType", 1.0), new("Size", "ContainerSize", 1.0),
             new("Window", "TimeWindow", 1.0), new("Priority", "Priority", 1.0),
             new("Notes", "Notes", 1.0), new("Date", "RequestedDate", 1.0),
             new("OrderNo", "ExternalId", 1.0)], []);

        var result = ImportPipeline.MapRows(data, mapping, "2026-01-01");
        var row = Assert.Single(result.Rows);

        Assert.Equal("Acme", row.CustomerName);
        Assert.Equal("Main", row.SiteName);
        Assert.Equal("123 St", row.SiteAddress);
        Assert.Equal("City", row.SiteCity);
        Assert.Equal("CA", row.SiteState);
        Assert.Equal("90210", row.SiteZip);
        Assert.Equal("DELIVERY", row.JobType);
        Assert.Equal("30", row.ContainerSize);
        Assert.Equal("MORNING", row.TimeWindow);
        Assert.Equal("HIGH", row.Priority);
        Assert.Equal("Gate code", row.Notes);
        Assert.Equal("2026-04-03", row.RequestedDate);
        Assert.Equal("WO-1", row.ExternalId);
    }
}
