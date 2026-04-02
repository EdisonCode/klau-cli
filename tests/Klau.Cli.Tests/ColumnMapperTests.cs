using Klau.Cli.Import;
using Xunit;

namespace Klau.Cli.Tests;

public class ColumnMapperTests
{
    [Fact]
    public void Map_ExactMatch_CustomerName()
    {
        var headers = new[] { "Customer Name" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("CustomerName", result.Matches[0].KlauField);
        Assert.Equal(1.0, result.Matches[0].Confidence);
    }

    [Fact]
    public void Map_CaseInsensitive_CustomerName()
    {
        var headers = new[] { "CUSTOMER NAME" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("CustomerName", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_UnderscoreSeparated_CustomerName()
    {
        var headers = new[] { "customer_name" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("CustomerName", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_HyphenSeparated_SiteAddress()
    {
        var headers = new[] { "site-address" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("SiteAddress", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_Abbreviation_State()
    {
        var headers = new[] { "st" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("SiteState", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_MultipleFields_AllMapped()
    {
        var headers = new[] { "Customer Name", "Street Address", "City", "State", "Zip", "Service Code" };
        var result = ColumnMapper.Map(headers);

        Assert.Equal(6, result.Matches.Count);
        Assert.Empty(result.UnmappedHeaders);

        var fields = result.Matches.Select(m => m.KlauField).ToHashSet();
        Assert.Contains("CustomerName", fields);
        Assert.Contains("SiteAddress", fields);
        Assert.Contains("SiteCity", fields);
        Assert.Contains("SiteState", fields);
        Assert.Contains("SiteZip", fields);
        Assert.Contains("JobType", fields);
    }

    [Fact]
    public void Map_UnknownColumn_AddedToUnmapped()
    {
        var headers = new[] { "Customer Name", "Foobar Column" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("CustomerName", result.Matches[0].KlauField);
        Assert.Single(result.UnmappedHeaders);
        Assert.Equal("Foobar Column", result.UnmappedHeaders[0]);
    }

    [Fact]
    public void Map_SubstringMatch_WorkOrder()
    {
        var headers = new[] { "Work Order" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("ExternalId", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_SubstringMatch_WO()
    {
        var headers = new[] { "WO" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("ExternalId", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_SubstringMatch_PONumber()
    {
        var headers = new[] { "PO" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("ExternalId", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_ExactMatch_ContainerSize()
    {
        var headers = new[] { "Container Size" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("ContainerSize", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_ShortAlias_Yards()
    {
        var headers = new[] { "Yards" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("ContainerSize", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_ExactMatch_Priority()
    {
        var headers = new[] { "Priority" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("Priority", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_SpecialInstructions_MapsToNotes()
    {
        var headers = new[] { "Special Instructions" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("Notes", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_DeliveryDate_MapsToRequestedDate()
    {
        var headers = new[] { "Delivery Date" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("RequestedDate", result.Matches[0].KlauField);
    }

    [Fact]
    public void Map_EmptyHeaders_HandledGracefully()
    {
        var headers = new[] { "", "  ", "Customer Name" };
        var result = ColumnMapper.Map(headers);

        Assert.Single(result.Matches);
        Assert.Equal("CustomerName", result.Matches[0].KlauField);
        Assert.Equal(2, result.UnmappedHeaders.Count);
    }

    [Fact]
    public void Map_NoDuplicateFieldAssignment()
    {
        // Both "customer" and "cust name" could map to CustomerName,
        // but only one should win.
        var headers = new[] { "Customer", "Cust Name" };
        var result = ColumnMapper.Map(headers);

        var customerMatches = result.Matches.Where(m => m.KlauField == "CustomerName").ToList();
        Assert.Single(customerMatches);
    }

    [Fact]
    public void Normalize_LowercasesAndReplacesDelimiters()
    {
        Assert.Equal("customer name", ColumnMapper.Normalize("Customer_Name"));
        Assert.Equal("site address", ColumnMapper.Normalize("site-address"));
        Assert.Equal("zip code", ColumnMapper.Normalize("  ZIP  CODE  "));
    }

    [Fact]
    public void Map_RealWorldHeaders_TypicalWasteHauler()
    {
        var headers = new[]
        {
            "Customer Name", "Street Address", "City", "State", "Zip Code",
            "Service Code", "Container", "WO Number", "Scheduled Date",
            "Priority", "Special Instructions"
        };

        var result = ColumnMapper.Map(headers);

        // All headers should map
        Assert.Equal(11, result.Matches.Count);
        Assert.Empty(result.UnmappedHeaders);

        var fields = result.Matches.Select(m => m.KlauField).ToList();
        Assert.Contains("CustomerName", fields);
        Assert.Contains("SiteAddress", fields);
        Assert.Contains("SiteCity", fields);
        Assert.Contains("SiteState", fields);
        Assert.Contains("SiteZip", fields);
        Assert.Contains("JobType", fields);
        Assert.Contains("ContainerSize", fields);
        Assert.Contains("ExternalId", fields);
        Assert.Contains("RequestedDate", fields);
        Assert.Contains("Priority", fields);
        Assert.Contains("Notes", fields);
    }
}
