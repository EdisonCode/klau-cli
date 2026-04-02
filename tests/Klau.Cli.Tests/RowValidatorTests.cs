using Klau.Cli.Domain;
using Xunit;

namespace Klau.Cli.Tests;

public class RowValidatorTests
{
    private static JobImportRow Row(
        string customer = "Acme Corp",
        string? address = "123 Main St",
        string? containerSize = "30",
        string? jobType = "DELIVERY",
        string? externalId = null,
        string? timeWindow = null,
        string? priority = null) => new()
    {
        CustomerName = customer,
        SiteAddress = address,
        ContainerSize = containerSize,
        JobType = jobType,
        ExternalId = externalId,
        TimeWindow = timeWindow,
        Priority = priority,
    };

    [Fact]
    public void Validate_ValidRows_NoWarnings()
    {
        var rows = new[] { Row(), Row(customer: "Beta LLC", externalId: "WO-1") };
        var result = RowValidator.Validate(rows);

        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.AddressMissingCount);
    }

    [Fact]
    public void Validate_MissingAddress_WarnsPerRow()
    {
        var rows = new[] { Row(address: null), Row(address: ""), Row(address: "  ") };
        var result = RowValidator.Validate(rows);

        Assert.Equal(3, result.AddressMissingCount);
        Assert.Contains(result.Warnings, w => w.Message.Contains("no address"));
    }

    [Fact]
    public void Validate_MajorityMissingAddresses_HasBlockingIssues()
    {
        var rows = new[] { Row(address: null), Row(address: null), Row(address: "123 St") };
        var result = RowValidator.Validate(rows);

        Assert.True(result.HasBlockingIssues);
    }

    [Fact]
    public void Validate_MinorityMissingAddresses_NotBlocking()
    {
        var rows = new[] { Row(), Row(), Row(), Row(address: null) };
        var result = RowValidator.Validate(rows);

        Assert.False(result.HasBlockingIssues);
    }

    [Fact]
    public void Validate_InvalidContainerSize_Warns()
    {
        var rows = new[] { Row(containerSize: "99") };
        var result = RowValidator.Validate(rows);

        Assert.Contains(result.Warnings, w => w.Message.Contains("non-standard container size"));
    }

    [Fact]
    public void Validate_ValidContainerSizes_NoWarning()
    {
        var sizes = new[] { "10", "15", "20", "30", "35", "40" };
        foreach (var size in sizes)
        {
            var result = RowValidator.Validate([Row(containerSize: size)]);
            Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("container size"));
        }
    }

    [Fact]
    public void Validate_InvalidJobType_Warns()
    {
        var rows = new[] { Row(jobType: "HAUL_AWAY") };
        var result = RowValidator.Validate(rows);

        Assert.Contains(result.Warnings, w => w.Message.Contains("unmapped job type"));
    }

    [Fact]
    public void Validate_ValidJobTypes_NoWarning()
    {
        var types = new[] { "DELIVERY", "PICKUP", "DUMP_RETURN", "SWAP" };
        foreach (var type in types)
        {
            var result = RowValidator.Validate([Row(jobType: type)]);
            Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("job type"));
        }
    }

    [Fact]
    public void Validate_InvalidTimeWindow_Warns()
    {
        var rows = new[] { Row(timeWindow: "EVENING") };
        var result = RowValidator.Validate(rows);

        Assert.Contains(result.Warnings, w => w.Message.Contains("unknown time window"));
    }

    [Fact]
    public void Validate_InvalidPriority_Warns()
    {
        var rows = new[] { Row(priority: "RUSH") };
        var result = RowValidator.Validate(rows);

        Assert.Contains(result.Warnings, w => w.Message.Contains("unknown priority"));
    }

    [Fact]
    public void Validate_DuplicateExternalIds_Warns()
    {
        var rows = new[] { Row(externalId: "WO-1"), Row(externalId: "WO-1") };
        var result = RowValidator.Validate(rows);

        Assert.Equal(1, result.DuplicateExternalIdCount);
        Assert.Contains(result.Warnings, w => w.Message.Contains("duplicate external ID"));
    }

    [Fact]
    public void Validate_UniqueExternalIds_NoWarning()
    {
        var rows = new[] { Row(externalId: "WO-1"), Row(externalId: "WO-2") };
        var result = RowValidator.Validate(rows);

        Assert.Equal(0, result.DuplicateExternalIdCount);
    }

    [Fact]
    public void Validate_NullFieldsAreOptional_NoWarning()
    {
        var rows = new[] { Row(containerSize: null, jobType: null, timeWindow: null, priority: null) };
        var result = RowValidator.Validate(rows);

        // Only address warning since it's null
        Assert.DoesNotContain(result.Warnings, w =>
            w.Message.Contains("container") || w.Message.Contains("job type") ||
            w.Message.Contains("time window") || w.Message.Contains("priority"));
    }

    [Fact]
    public void Validate_MultipleIssues_GroupedCleanly()
    {
        var rows = new[]
        {
            Row(address: null, containerSize: "99", externalId: "WO-1"),
            Row(address: null, containerSize: "XL", externalId: "WO-1"),
            Row(containerSize: "30", externalId: "WO-2"),
        };

        var result = RowValidator.Validate(rows);

        Assert.Equal(2, result.AddressMissingCount);
        Assert.Equal(1, result.DuplicateExternalIdCount);
        // Container size issues are grouped into one warning
        Assert.Single(result.Warnings, w => w.Message.Contains("non-standard container size"));
    }
}
