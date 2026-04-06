# Klau CLI

Import job data from CSV or Excel files into Klau and get optimized dispatch plans — no code required.

Built for ops teams, dispatchers, and billing clerks who export daily work orders from their existing system and need them loaded into Klau for dispatch optimization.

## Install

```bash
dotnet tool install -g Klau.Cli
```

Requires [.NET 9.0 runtime](https://dotnet.microsoft.com/download/dotnet/9.0) or later.

## Get Started

### Step 1: Log in

```bash
klau login
```

This will prompt for your Klau email and password, then automatically create and store an API key. You only need to do this once — the key is saved to `~/.config/klau/credentials.json` and used for all future commands.

If you already have an API key from Settings > Developer in the Klau dashboard:

```bash
klau login --api-key kl_live_your_key_here
```

### Step 2: Preview your data

Before importing, preview how your file will be mapped. This sends nothing to Klau — it just reads your file and shows you what it found:

```bash
klau import daily-orders.csv --dry-run
```

or with an Excel file:

```bash
klau import work-orders.xlsx --dry-run
```

You'll see which columns were mapped, any data quality warnings, and a preview of the first 5 rows. Fix any issues in your export before proceeding.

### Step 3: Import

```bash
klau import daily-orders.csv --date 2026-04-03
```

The CLI will:
1. Check that your Klau account is set up (drivers, trucks, yards, dump sites)
2. Validate your data (addresses, container sizes, job types)
3. Import jobs into Klau
4. Report results

### Step 4: Import + Optimize

```bash
klau import daily-orders.csv --date 2026-04-03 --optimize
```

This imports your jobs and then runs Klau's dispatch optimizer. You'll get back a plan grade, flow score, and assignment summary.

To also export the optimized dispatch plan as a CSV:

```bash
klau import daily-orders.csv --date 2026-04-03 --optimize --export dispatch-plan.csv
```

## Supported File Formats

| Format | Extensions | Notes |
|--------|-----------|-------|
| CSV | `.csv`, `.tsv`, `.txt` | Auto-detects delimiter (comma, tab, semicolon, pipe) |
| Excel | `.xlsx`, `.xls` | Reads the first worksheet, first row as headers |

The CLI handles quoted fields, BOM markers, and mixed line endings automatically.

## Column Mapping

The CLI automatically maps your column headers to Klau's job fields. It recognizes header names from common hauler dispatch systems — no configuration needed for most exports.

### How it works

1. **Exact match** — `Customer Name` → CustomerName
2. **Fuzzy match** — `Cust`, `Service Name`, `Billing Name` → CustomerName
3. **Prefix match** — `C_ADDR1`, `C_CITY`, `C_STATE` → SiteAddress, SiteCity, SiteState

### Recognized headers

| Klau Field | Your column might be called |
|---|---|
| **CustomerName** | Customer Name, Cust, Account Name, Service Name, Billing Name, Company |
| **SiteAddress** | Address, Street, Service Address, C_ADDR1, FullAddy, Full Address |
| **SiteCity** | City, Site City, Service City, C_CITY |
| **SiteState** | State, ST, Service State, C_STATE |
| **SiteZip** | Zip, Zip Code, Site Zip, C_ZIP |
| **JobType** | Service, Type, Job Type, Service Type, Service Code, Service Description |
| **ContainerSize** | Container, Size, Container Size, Size Value, Yard, Yards |
| **ExternalId** | Order #, Order Nbr, Order Number, Work Order, WO, PO, Reference, Ticket |
| **RequestedDate** | Date, Service Date, Requested Date, Delivery Date, Scheduled Date |
| **Notes** | Notes, Instructions, Comments, Special Instructions, Billing Notes |
| **Priority** | Priority, Order Priority |
| **TimeWindow** | Time Window, Delivery Window |

### Saving and reusing mappings

On first import, the CLI saves the inferred mapping to `.klau-mapping.json` next to your file. Future imports from that directory reuse the saved mapping automatically — no re-inference needed.

To use a specific mapping file:

```bash
klau import orders.csv --mapping /shared/our-column-mapping.json
```

## Data Validation

The CLI validates your data before sending it to Klau and gives you clear guidance when something needs fixing.

### Pre-flight account check

Before importing, the CLI verifies your Klau account is set up for dispatch:

```
  Pre-flight readiness check:
    ✗ Drivers: not configured
    → Add drivers: Klau dashboard > Drivers, or via SDK
    ✗ Trucks: not configured
    → Add trucks: Klau dashboard > Trucks, or via SDK
    ✓ Account ready (85% configured)
```

### Row-level validation

The CLI catches data quality issues before they hit the API:

- **Missing addresses** — "Row 5: no address for 'Acme Corp' — geocoding will fail, job may not be routable"
- **Invalid container sizes** — "3 rows have non-standard container size ('99') — expected: 10, 15, 20, 30, 35, 40"
- **Unmapped job types** — "8 rows have unmapped job type ('RO-DUMP & RETURN3L') — configure service code mappings in Settings > Company"
- **Duplicate order numbers** — "2 duplicate external IDs — duplicates will be rejected by the API"

If more than half your rows are missing addresses, the CLI flags it as a likely column mapping error and suggests re-running with `--dry-run`.

## Automated Import (Watch Mode)

For daily automation — e.g., your billing system drops a CSV export to a shared folder every morning — use watch mode:

```bash
klau import watch --folder /exports --pattern "*.csv" --optimize
```

### What watch mode does

- Monitors the folder for new CSV/XLSX files
- Waits for each file to finish writing before processing
- Runs the full import + validation + optimize flow
- Moves successful files to `processed/`
- Moves failed files to `failed/` with a `.error` file explaining what went wrong
- Writes dispatch plans to `output/` (when `--optimize` is set)

### Designed for reliability

Watch mode is built to run unattended for months:

- **Lock file** — Prevents two watchers on the same folder
- **Heartbeat** — Writes `.klau-heartbeat` every 60 seconds (monitor for staleness)
- **Error recovery** — A bad file doesn't crash the watcher; it's moved to `failed/` and processing continues
- **Retention** — Processed files are cleaned up after 30 days (configurable with `--retain-days`)
- **Graceful shutdown** — Responds to Ctrl+C and SIGTERM cleanly

### Watch mode options

| Option | Description | Default |
|---|---|---|
| `--folder` | Folder to watch (required) | — |
| `--pattern` | File glob pattern | `*.*` |
| `--date` | Dispatch date, or `today` for dynamic | today |
| `--optimize` | Run optimization after each import | false |
| `--retain-days` | Days to keep processed files | 30 |

## Authentication

### Key resolution

The CLI checks for your API key in this order:

1. `--api-key` flag (highest priority)
2. `KLAU_API_KEY` environment variable
3. `~/.config/klau/credentials.json` (stored by `klau login`)

### Commands

```bash
klau login                          # Interactive: email/password → auto-create API key
klau login --api-key kl_live_...    # Store an existing key
klau status                         # Show current auth state
klau logout                         # Remove stored credentials
```

### For non-admin users

The interactive `klau login` flow creates an API key automatically, which requires admin access in Klau. If you're not an admin, ask your admin to generate a key for you in Settings > Developer, then use:

```bash
klau login --api-key kl_live_the_key_they_gave_you
```

## Standalone Optimization

Already imported jobs through the dashboard or SDK? Run optimization separately:

```bash
klau optimize --date 2026-04-03
```

### Optimization modes

| Mode | Description |
|---|---|
| `full-day` | Re-optimize the entire day (default) |
| `new-job` | Fit newly added jobs into the existing plan |
| `rebalance` | Rebalance assignments across drivers |

```bash
klau optimize --date 2026-04-03 --mode new-job --export dispatch-plan.csv
```

## Diagnostics

```bash
klau doctor
```

Checks your environment, authentication, API connectivity, and account configuration in one command. Shows exactly what's working and what needs attention.

## Multi-Tenant Support

For companies with multiple divisions or subsidiaries, each with their own fleet:

```bash
klau tenant list                    # List available tenants
klau tenant set <tenant-id>         # Set default tenant for all commands
klau import orders.csv --tenant <id>  # One-off tenant override
```

## AI Agent Integration (MCP)

The CLI can run as an [MCP server](https://modelcontextprotocol.io/) for AI agents like Claude Desktop, Cursor, and other MCP-compatible tools:

```bash
klau mcp
```

This exposes `import`, `optimize`, `doctor`, and `status` as MCP tools over stdio. AI agents can call them directly with structured parameters and get JSON responses — no shell parsing needed.

### MCP client configuration

Add to your MCP client config (e.g., Claude Desktop `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "klau": {
      "command": "klau",
      "args": ["mcp", "--api-key", "kl_live_your_key_here"]
    }
  }
}
```

If you've already run `klau login`, you can omit `--api-key` — the MCP server uses stored credentials.

## JSON Output

All commands support structured JSON output for scripting and CI/CD:

```bash
klau import orders.csv --output json
klau optimize --date 2026-04-03 --output json
klau doctor --output json
```

Every command emits a consistent envelope:

```json
{
  "command": "import",
  "status": "success",
  "exitCode": 0,
  "data": { "imported": 45, "skipped": 2 },
  "error": null
}
```

### Non-interactive mode

For CI/CD pipelines and automation scripts, use `--yes` to skip all interactive prompts:

```bash
klau import orders.csv --date 2026-04-03 --optimize --yes
```

## Shell Completions

Enable tab completion for your shell:

```bash
# Bash
echo 'eval "$(klau completion bash)"' >> ~/.bashrc

# Zsh
echo 'eval "$(klau completion zsh)"' >> ~/.zshrc

# Fish
klau completion fish | source

# PowerShell
klau completion powershell >> $PROFILE
```

## Troubleshooting with --verbose

When something isn't working, `--verbose` shows HTTP request details on stderr:

```bash
klau doctor --verbose
```

```
  [verbose] GET /api/v1/companies/me -> 200 (805ms)
  [verbose] GET /api/v1/companies/go-live-readiness -> 200 (534ms)
```

Share this output with support to diagnose connectivity or API issues.

## Updating

```bash
klau upgrade
```

The CLI checks for updates in the background and will notify you when a new version is available.

## All Commands

| Command | Description |
|---|---|
| `klau login` | Authenticate and store credentials |
| `klau logout` | Remove stored credentials |
| `klau status` | Show auth state and configuration |
| `klau doctor` | Diagnose environment, auth, and account readiness |
| `klau import <file>` | Import a CSV or XLSX file |
| `klau import watch` | Watch a folder for files to import automatically |
| `klau optimize` | Run dispatch optimization for a date |
| `klau tenant list` | List available tenants |
| `klau tenant set` | Set the default tenant |
| `klau mcp` | Run as an MCP server for AI agents |
| `klau completion <shell>` | Generate shell completion scripts |
| `klau upgrade` | Update to the latest version |

### Global options

| Option | Description |
|---|---|
| `--api-key` | Override stored API key |
| `--tenant` | Override default tenant for this command |
| `--output json` | JSON output mode |
| `--yes` / `-y` | Skip interactive prompts |
| `--no-color` | Suppress colored output (also via `NO_COLOR` env var) |
| `--verbose` / `-v` | Show HTTP request details on stderr |

### Import options

| Option | Description | Default |
|---|---|---|
| `--date` | Dispatch date (YYYY-MM-DD) | today |
| `--mapping` | Path to column mapping file | auto-detected |
| `--optimize` | Run dispatch optimization | false |
| `--export` | Export dispatch plan CSV | — |
| `--dry-run` | Preview mapping and validate without importing | false |

### Optimize options

| Option | Description | Default |
|---|---|---|
| `--date` | Dispatch date (YYYY-MM-DD) | today |
| `--mode` | Optimization mode: full-day, new-job, rebalance | full-day |
| `--export` | Export dispatch plan CSV | — |

## Example: Daily Operations Workflow

**Morning — one command:**
```bash
klau import todays-orders.xlsx --date 2026-04-03 --optimize --export dispatch-plan.csv
```

**Result:**
```
  Reading todays-orders.xlsx... 47 rows (XLSX)

  Column mapping:
    Customer Name   →  CustomerName
    Service Address →  SiteAddress
    Service City    →  SiteCity
    Service State   →  SiteState
    Site Zip        →  SiteZip
    Size Value      →  ContainerSize
    Order Nbr       →  ExternalId
    Service Date    →  RequestedDate

  Preview (5 of 47 rows):
    Customer                Address                 City          Type      ExternalId
    ----------------------  ----------------------  -----------   --------  ----------
    Acme Construction       123 Industrial Way      Springfield   DELIVERY  WO-1001
    Metro Demolition        456 Oak Ave             Shelbyville   PICKUP    WO-1002
    Harbor Freight          789 Commerce Dr         Capital City  SWAP      WO-1003

  Pre-flight readiness check:
    ✓ Account ready (100% configured)

  Importing 47 jobs for 2026-04-03...
    ✓ Imported: 45  |  Skipped: 2
    ✓ Auto-created: 3 customers, 5 sites
    ! Row 12: invalid container size "ABC"

  Optimizing dispatch...
    ✓ Grade: A (92/100)  |  Flow: 87/100
    ✓ Assigned: 43/45  |  Unassigned: 2

  Exported to dispatch-plan.csv

  Done in 14.2s
```

**Overnight automation:**
```bash
klau import watch --folder /exports --optimize
```

The billing system drops a file at 4 AM. By 5 AM, the dispatch plan is waiting in `output/`.

## Troubleshooting

### "No API key found"

Run `klau login` to authenticate, or set the `KLAU_API_KEY` environment variable.

### "Blocking issues must be resolved before import"

Your Klau account is missing required configuration (drivers, trucks, yards, or dump sites). Set these up in the Klau dashboard before importing jobs.

### "Over half the rows are missing addresses"

The address column in your file likely isn't being mapped correctly. Run `klau import <file> --dry-run` to see the column mapping and fix it. You can create a custom `.klau-mapping.json` to override the auto-detection.

### "Unmapped job type"

Your file uses service codes that Klau doesn't recognize (e.g., "RO-DUMP & RETURN3L"). Configure service code mappings in Settings > Company in the Klau dashboard to map your codes to Klau job types (DELIVERY, PICKUP, DUMP_RETURN, SWAP).

### Column mapping is wrong

Delete the `.klau-mapping.json` file next to your CSV and re-run with `--dry-run`. The CLI will re-infer the mapping. If auto-detection doesn't work for your format, create a manual mapping file:

```json
{
  "Your Customer Column": "CustomerName",
  "Your Address Column": "SiteAddress",
  "Your City Column": "SiteCity",
  "Your State Column": "SiteState",
  "Your Zip Column": "SiteZip",
  "Your Service Column": "JobType",
  "Your Container Column": "ContainerSize",
  "Your Order # Column": "ExternalId"
}
```

## License

MIT
