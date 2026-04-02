# Klau CLI

Import CSV job data into Klau and get optimized dispatch plans -- no code required.

Built for ops managers, dispatchers, and billing clerks who need to get daily job data into Klau quickly and reliably from existing spreadsheet exports.

## Install

```bash
dotnet tool install -g Klau.Cli
```

## Quick Start

```bash
# Set your API key (get one from app.klau.com/settings/api)
export KLAU_API_KEY=kl_live_...

# Import a CSV file
klau import daily-orders.csv --date 2026-04-03

# Import and optimize dispatch
klau import daily-orders.csv --date 2026-04-03 --optimize

# Import, optimize, and export the dispatch plan
klau import daily-orders.csv --date 2026-04-03 --optimize --export dispatch-plan.csv
```

## How It Works

1. **Read** -- The CLI reads your CSV file, detecting delimiters (comma, tab, semicolon, pipe) and handling quoted fields, BOM markers, and mixed line endings automatically.

2. **Map** -- Column headers are fuzzy-matched to Klau job fields. The CLI recognizes dozens of common header names used by waste haulers (`Customer Name`, `Cust`, `Account Name`, `WO Number`, `Service Code`, etc.).

3. **Preview** -- Before sending anything, the CLI shows you the column mapping and a preview of the first 5 rows so you can verify everything looks right.

4. **Import** -- Jobs are sent to the Klau API. The CLI reports successes, skips (duplicate ExternalIds), auto-created customers/sites, and any row-level validation errors.

5. **Optimize** (optional) -- With `--optimize`, the CLI kicks off Klau's dispatch optimization and reports the grade, flow score, and assignment counts.

6. **Export** (optional) -- With `--export`, the optimized dispatch plan is written to a CSV file for printing or sharing.

## Column Mapping

The CLI automatically maps your CSV headers to Klau fields. It uses a three-pass approach:

1. **Exact match** -- `Customer Name` matches `CustomerName` directly
2. **Substring match** -- `Street Address` matches `SiteAddress` via the `address` alias
3. **Token overlap** -- `Delivery Date Requested` matches `RequestedDate` via shared tokens

### Supported Fields

| Klau Field      | Recognized Headers                                                                 |
|-----------------|------------------------------------------------------------------------------------|
| CustomerName    | customer, cust name, customer name, account name                                   |
| SiteName        | site, site name, location, job site                                                |
| SiteAddress     | address, street, site address, addr, address1                                      |
| SiteCity        | city, site city                                                                    |
| SiteState       | state, st, site state                                                              |
| SiteZip         | zip, zipcode, zip code, postal, site zip                                           |
| JobType         | type, job type, service, service type, service code                                |
| ContainerSize   | size, container, container size, yard, yards                                       |
| TimeWindow      | window, time window, time, delivery window                                         |
| Priority        | priority                                                                           |
| Notes           | notes, instructions, comments, special instructions                                |
| RequestedDate   | date, requested date, request date, delivery date, scheduled                       |
| ExternalId      | external, external id, order, order number, work order, wo, po, reference          |

### Saving Mappings

On first import, the CLI saves the inferred mapping to `.klau-mapping.json` in the same directory as your CSV. Future imports from that directory reuse the saved mapping automatically.

You can also point to a specific mapping file:

```bash
klau import orders.csv --mapping /shared/our-mapping.json
```

## File Watcher (Automation)

For automated workflows (e.g., an ERP that drops CSV exports to a shared folder), use watch mode:

```bash
klau import watch --folder /exports --pattern "*.csv" --optimize
```

Watch mode will:
- Monitor the folder for new files matching the pattern
- Wait for each file to finish writing (stable file size check)
- Run the full import + optimize flow
- Move processed files to a `processed/` subfolder
- Write dispatch plans to an `output/` subfolder

## Commands

### `klau import <file>`

Import a CSV file into Klau.

| Option       | Description                                          | Default   |
|--------------|------------------------------------------------------|-----------|
| `--date`     | Dispatch date (YYYY-MM-DD)                           | today     |
| `--mapping`  | Path to `.klau-mapping.json`                         | auto      |
| `--optimize` | Run dispatch optimization after import               | false     |
| `--export`   | Export dispatch plan to a CSV path                   | none      |
| `--api-key`  | Klau API key (overrides `KLAU_API_KEY` env var)      | env var   |

### `klau import watch`

Watch a folder for new CSV files and auto-import.

| Option       | Description                                          | Default   |
|--------------|------------------------------------------------------|-----------|
| `--folder`   | Folder to watch (required)                           | --        |
| `--pattern`  | File glob pattern                                    | `*.csv`   |
| `--date`     | Dispatch date                                        | today     |
| `--optimize` | Run optimization after each import                   | false     |
| `--api-key`  | Klau API key                                         | env var   |

### `klau config init`

Interactive first-time setup. Prompts for your API key and shows how to persist it.

## Example Output

```
$ klau import daily-orders.csv --date 2026-04-03 --optimize

  Reading daily-orders.csv... 47 rows

  Column mapping (inferred):
    Customer Name  ->  CustomerName
    Street Address ->  SiteAddress
    City           ->  SiteCity
    State          ->  SiteState
    Zip            ->  SiteZip
    Service Code   ->  JobType
    Container      ->  ContainerSize
    WO Number      ->  ExternalId

  Saved mapping to .klau-mapping.json

  Importing 47 jobs for 2026-04-03...
    v Imported: 45  |  Skipped: 2
    v Auto-created: 3 customers, 5 sites
    ! Row 12: invalid container size "ABC"
    ! Row 31: missing customer name

  Optimizing dispatch...
    v Grade: A (92/100)  |  Flow: 87/100
    v Assigned: 43/45  |  Unassigned: 2

  Done in 12.3s
```

## Requirements

- .NET 9.0 runtime or later
- A Klau API key (`kl_live_...`)

## License

MIT
