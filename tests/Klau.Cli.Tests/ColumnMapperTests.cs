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

    [Fact]
    public void Map_RealWorldHeaders_LargeDispatchExport()
    {
        // Real 59-column header set from a large hauler dispatch system
        var headers = new[]
        {
            "Selection", "Sequence #", "Order Nbr", "Order Status", "Service",
            "Order Priority", "Account Nbr", "Order Action", "Service Date",
            "Driver", "Service Name", "Service Address", "Service City",
            "Vehicle", "Route", "Service State", "Amount", "Service Description",
            "Instructions", "Call In By", "Call Date", "Waiver Signed", "Phone",
            "Recvd By", "Order Created On", "Display Details", "Size Value",
            "Site Status", "Original Driver", "L 7", "Dispatch", "Reviewed By",
            "Billing Notes", "Service Qty", "Service Area", "PO Release Nbr",
            "Entered By", "Billing Name", "Service Name", "Vendor Name",
            "Site Zip", "Time Started", "Arrival Time", "Departure Time",
            "Arrived Landfill", "Departed Landfill", "Returned Time", "Time End",
            "Service Phone", "Material Qty - Billed", "Material Rate - Billed",
            "Material Subtotal - Billed", "Material Tax Fee - Billed", "Total",
            "Material ", "Site Name 2", "Leed Required", "Material Vendor",
            "Route Supervisor"
        };

        var result = ColumnMapper.Map(headers);
        var fields = result.Matches.ToDictionary(m => m.KlauField, m => m.CsvHeader);

        // Must map the critical fields from this real export
        Assert.True(fields.ContainsKey("CustomerName"), "Should map a customer name column");
        Assert.True(fields.ContainsKey("SiteAddress"), "Should map Service Address");
        Assert.True(fields.ContainsKey("SiteCity"), "Should map Service City");
        Assert.True(fields.ContainsKey("SiteState"), "Should map Service State");
        Assert.True(fields.ContainsKey("SiteZip"), "Should map Site Zip");
        Assert.True(fields.ContainsKey("ContainerSize"), "Should map Size Value");
        Assert.True(fields.ContainsKey("ExternalId"), "Should map Order Nbr");
        Assert.True(fields.ContainsKey("RequestedDate"), "Should map Service Date");
        Assert.True(fields.ContainsKey("Notes"), "Should map Instructions or Billing Notes");
        Assert.True(fields.ContainsKey("Priority"), "Should map Order Priority");

        // Verify specific high-value mappings
        Assert.Equal("Service Address", fields["SiteAddress"]);
        Assert.Equal("Service City", fields["SiteCity"]);
        Assert.Equal("Size Value", fields["ContainerSize"]);
        Assert.Equal("Order Nbr", fields["ExternalId"]);
        Assert.Equal("Service Date", fields["RequestedDate"]);
    }

    [Fact]
    public void Map_RealWorldHeaders_CompactDispatchExport()
    {
        // Real 14-column header set from a compact hauler dispatch CSV export
        var headers = new[]
        {
            "Order #", "Acct #", "Customer Name", "C_ADDR1", "C_ADDRNUM1",
            "C_CITY", "C_STATE", "C_ZIP", "Container", "Service",
            "Destination", "longitude", "latitude", "FullAddy"
        };

        var result = ColumnMapper.Map(headers);
        var fields = result.Matches.ToDictionary(m => m.KlauField, m => m.CsvHeader);

        Assert.Equal("Order #", fields["ExternalId"]);
        Assert.Equal("Customer Name", fields["CustomerName"]);
        Assert.Equal("C_ADDR1", fields["SiteAddress"]);
        Assert.Equal("C_CITY", fields["SiteCity"]);
        Assert.Equal("C_STATE", fields["SiteState"]);
        Assert.Equal("C_ZIP", fields["SiteZip"]);
        Assert.Equal("Container", fields["ContainerSize"]);
        Assert.Equal("Service", fields["JobType"]);
    }
}
