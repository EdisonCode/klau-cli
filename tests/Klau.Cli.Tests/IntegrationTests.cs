using System.Diagnostics;
using System.Text;
using ClosedXML.Excel;
using Xunit;

namespace Klau.Cli.Tests;

/// <summary>
/// Integration tests that invoke the CLI as a real process and assert on
/// exit codes, stdout, and file system side effects.
///
/// --dry-run tests exercise the full pipeline (read → map → validate)
/// without needing a live Klau API.
/// </summary>
public class IntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public IntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"klau-cli-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, true);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string WriteCsv(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteXlsx(string name, string[] headers, params string[][] rows)
    {
        var path = Path.Combine(_tempDir, name);
        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("Sheet1");
        for (var col = 0; col < headers.Length; col++)
            ws.Cell(1, col + 1).Value = headers[col];
        for (var row = 0; row < rows.Length; row++)
            for (var col = 0; col < rows[row].Length; col++)
                ws.Cell(row + 2, col + 1).Value = rows[row][col];
        workbook.SaveAs(path);
        return path;
    }

    private static (int ExitCode, string Output) RunCli(params string[] args)
    {
        // Find the CLI project directory
        var solutionDir = FindSolutionDir();
        var projectPath = Path.Combine(solutionDir, "src", "Klau.Cli", "Klau.Cli.csproj");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectPath}\" -- {string.Join(" ", args)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Clear KLAU_API_KEY to avoid interference from the dev environment
        psi.Environment["KLAU_API_KEY"] = "";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        return (process.ExitCode, stdout + stderr);
    }

    private static string FindSolutionDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir!, "Klau.Cli.sln")))
                return dir!;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find Klau.Cli.sln");
    }

    // ── Exit Code Tests ─────────────────────────────────────────────────────

    [Fact]
    public void Import_MissingFile_ReturnsInputError()
    {
        var (exitCode, output) = RunCli("import", "/nonexistent/file.csv", "--dry-run");

        Assert.Equal(2, exitCode);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_InvalidDateFormat_ReturnsInputError()
    {
        var csv = WriteCsv("test.csv", "Customer Name\nAcme Corp");
        var (exitCode, output) = RunCli("import", csv, "--date", "April-3rd", "--dry-run");

        Assert.Equal(2, exitCode);
        Assert.Contains("Invalid date format", output);
    }

    [Fact]
    public void Import_UnsupportedFormat_ReturnsInputError()
    {
        var path = Path.Combine(_tempDir, "data.pdf");
        File.WriteAllText(path, "not a pdf");
        var (exitCode, output) = RunCli("import", path, "--dry-run");

        Assert.Equal(2, exitCode);
        Assert.Contains("Unsupported", output);
    }

    [Fact]
    public void Import_NoApiKey_NoDryRun_ReturnsConfigError()
    {
        var csv = WriteCsv("test.csv", "Customer Name\nAcme Corp");
        var (exitCode, output) = RunCli("import", csv);

        Assert.Equal(1, exitCode);
        Assert.Contains("No API key", output);
        Assert.Contains("KLAU_API_KEY", output);
    }

    // ── Dry Run Tests ───────────────────────────────────────────────────────

    [Fact]
    public void Import_DryRun_Csv_Success()
    {
        var csv = WriteCsv("orders.csv",
            "Customer Name,Street Address,City,State,Zip,Container Size,Order Number\n" +
            "Acme Corp,123 Main St,Springfield,IL,62701,30,WO-001\n" +
            "Beta LLC,456 Oak Ave,Shelbyville,IL,62702,20,WO-002\n");

        var (exitCode, output) = RunCli("import", csv, "--date", "2026-04-03", "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("2 rows", output);
        Assert.Contains("Dry run complete", output);
        Assert.Contains("2 rows mapped", output);
    }

    [Fact]
    public void Import_DryRun_Xlsx_Success()
    {
        var xlsx = WriteXlsx("orders.xlsx",
            ["Customer Name", "Service Address", "Service City", "Size Value", "Order Nbr"],
            ["Acme Corp", "123 Main St", "Springfield", "30", "WO-001"],
            ["Beta LLC", "456 Oak Ave", "Shelbyville", "20", "WO-002"]);

        var (exitCode, output) = RunCli("import", xlsx, "--date", "2026-04-03", "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("2 rows", output);
        Assert.Contains("XLSX", output);
        Assert.Contains("Dry run complete", output);
    }

    [Fact]
    public void Import_DryRun_ShowsColumnMapping()
    {
        var csv = WriteCsv("mapped.csv",
            "Customer Name,Street Address,City,State,Zip Code\n" +
            "Acme Corp,123 Main St,Springfield,IL,62701\n");

        var (exitCode, output) = RunCli("import", csv, "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("Column mapping", output);
        Assert.Contains("CustomerName", output);
        Assert.Contains("SiteAddress", output);
    }

    [Fact]
    public void Import_DryRun_SavesMappingFile()
    {
        var csv = WriteCsv("automapped.csv",
            "Customer Name,City\n" +
            "Acme Corp,Springfield\n");

        // Remove any existing mapping
        var mappingPath = Path.Combine(_tempDir, ".klau-mapping.json");
        if (File.Exists(mappingPath)) File.Delete(mappingPath);

        RunCli("import", csv, "--dry-run");

        Assert.True(File.Exists(mappingPath), "Should save .klau-mapping.json");
        var json = File.ReadAllText(mappingPath);
        Assert.Contains("CustomerName", json);
    }

    [Fact]
    public void Import_DryRun_MissingCustomerColumn_ShowsWarnings()
    {
        var csv = WriteCsv("nocustomer.csv",
            "Address,City\n" +
            "123 Main St,Springfield\n");

        var (exitCode, output) = RunCli("import", csv, "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("missing customer name", output);
        Assert.Contains("0 rows mapped", output);
    }

    [Fact]
    public void Import_DryRun_EmptyCsv_ReturnsInputError()
    {
        var csv = WriteCsv("empty.csv", "");

        var (exitCode, _) = RunCli("import", csv, "--dry-run");

        Assert.Equal(2, exitCode);
    }

    // ── Mapping File Tests ──────────────────────────────────────────────────

    [Fact]
    public void Import_DryRun_UsesExistingMapping()
    {
        // Write a custom mapping
        var mappingPath = Path.Combine(_tempDir, "custom-mapping.json");
        File.WriteAllText(mappingPath,
            """{"Company":"CustomerName","Addr":"SiteAddress","Town":"SiteCity"}""");

        var csv = WriteCsv("custom.csv",
            "Company,Addr,Town\n" +
            "Acme Corp,123 Main St,Springfield\n");

        var (exitCode, output) = RunCli("import", csv, "--mapping", mappingPath, "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("1 rows mapped", output);
    }

    [Fact]
    public void Import_DryRun_BadMappingFile_ReturnsInputError()
    {
        var mappingPath = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(mappingPath, "this is not json{{{");

        var csv = WriteCsv("test.csv", "Customer Name\nAcme Corp");

        var (exitCode, _) = RunCli("import", csv, "--mapping", mappingPath, "--dry-run");

        // Should fail gracefully, not crash
        Assert.True(exitCode >= 1);
    }

    // ── Real-World Format Tests ─────────────────────────────────────────────

    [Fact]
    public void Import_DryRun_LargeDispatchFormat()
    {
        // Simulated 59-column hauler dispatch system export
        var xlsx = WriteXlsx("wc-export.xlsx",
            ["Selection", "Sequence #", "Order Nbr", "Order Status", "Service",
             "Order Priority", "Account Nbr", "Order Action", "Service Date",
             "Driver", "Service Name", "Service Address", "Service City",
             "Vehicle", "Route", "Service State", "Amount", "Service Description",
             "Instructions", "Call In By", "Call Date", "Waiver Signed", "Phone",
             "Recvd By", "Order Created On", "Display Details", "Size Value",
             "Site Status"],
            ["Checked", "0", "1241747", "OPEN", "18-NT", "NORMAL", "1003000", "SERVICE",
             "2026-02-19", "DRIVER 1", "LAND O LAKES", "22ND ST SW 1700", "WILLMAR",
             "RO27", "W3-ROLLOFF", "MN", "0", "40YD COMPACTOR", "BOX #42 IN YARD",
             "", "", "", "", "", "", "", "40", "A-ACTIVE"]);

        var (exitCode, output) = RunCli("import", xlsx, "--date", "2026-04-03", "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("1 rows", output);
        Assert.Contains("XLSX", output);
        Assert.Contains("Dry run complete", output);
        Assert.Contains("CustomerName", output); // Column mapping should find it
    }
}
