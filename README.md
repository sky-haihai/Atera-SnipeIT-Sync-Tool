# Atera Snipe-IT Auto Sync

[简体中文](README.zh-CN.md) | [English](README.md)

Atera Snipe-IT Auto Sync is a Windows asset synchronization tool. It pulls managed agents from Atera, converts device, customer, manufacturer, model, and hardware identity data into Snipe-IT asset records, and runs either on a Windows Service schedule or on demand from the Tray App.

Developed by **[VUE IT Inc.](https://vueit.ca/)**

Current version: `1.0.1`<br>
Publisher: [VUE IT Inc.](https://vueit.ca/)<br>
Platform: Windows x64

> [!IMPORTANT]
> `Sync Now` and enabled schedules make real changes in Snipe-IT, including soft-deleting `ATERA-` assets that have disappeared from Atera. Run `Preview` and review every generated CSV before first use, after changing mapping settings, and after an upgrade.

## How it works

```text
Atera agents
    ↓ Complete paged read
Field reconstruction and normalization
    ↓
Snipe-IT references / hardware snapshot
    ↓ Matching, conflict checks, and change planning
Company / Category / Model / Asset create or update
    ↓
Stale ATERA- asset soft-delete
    ↓
History / Logs / Notifications
```

The Tray App only manages configuration, displays status, and sends commands to the Worker. The background Windows Service `AteraSnipeItAutoSync` performs all API reads and synchronization work.

## Key features

- Reads managed agents from the official Atera API with complete pagination and handles authentication errors, rate limits, transient failures, invalid pagination, and malformed records.
- Generates a stable `ATERA-{AteraAgentId}` Asset Tag for each device.
- Maps Atera customer, manufacturer, model, device name, serial, MAC, device type, and audit notes into Snipe-IT.
- Supports Company and Manufacturer aliases, such as `Dell Inc.=Dell`.
- Can create missing Companies and Models when enabled. It creates a missing target Asset Category when required.
- Matches assets by strong identities such as Asset Tag, MAC, and reliable serial. It uses high-confidence name matching within an identical Company + Category + Model context only when no strong identity matches.
- Detects duplicate source IDs, Asset Tags, reliable serials, and non-ignored MAC addresses so conflicting records cannot overwrite the wrong asset.
- Supports a Snipe-IT MAC custom field and Fieldset, including MAC writes, Model Fieldset assignment, and Model Category normalization.
- `Preview` is a true dry run: it can read Atera and Snipe-IT but sends no `POST`, `PATCH`, or `DELETE` requests, and it produces four CSV change plans.
- Supports manual `Sync Now`, Daily / Weekly / Monthly schedules, multiple run times per day, Windows time zones, and non-overlapping execution.
- The Dashboard shows Worker / Service / Schedule status, the next run time, current activity, and Created / Updated / No change / Deleted counts from the latest real sync.
- Writes structured local History for every sync run and supports Email, Teams Workflow Adaptive Card, and Generic JSON webhook notifications.
- Ships as a self-contained Windows x64 MSI, so the target machine does not need a preinstalled .NET Runtime.

## Fail-closed deletion safety

Automatic deletion is intentionally fail-closed instead of attempting to delete whenever possible.

- Only Snipe-IT Hardware Assets whose Asset Tag starts with `ATERA-` belong to the automatic synchronization namespace. Manually managed assets are never automatically deleted.
- The tool calculates `MissingFromAtera` only after Atera pagination completes successfully and a complete Snipe-IT hardware snapshot is available.
- It compares complete Asset Tags between the current Atera inventory and Snipe-IT. An asset becomes a soft-delete candidate only when it exists in Snipe-IT, is in the `ATERA-` namespace, and is absent from the current Atera inventory.
- A duplicate source record receives `SnipeImport.DuplicateBatchIdentity` and is blocked individually. Unrelated healthy records may still be created or updated.
- However, any source validation, reference, matching, or duplicate-target failure during planning clears every `MissingFromAtera` deletion plan for that run.
- The Asset Tag of a blocked source record still counts as part of the current Atera inventory, preventing a temporary validation issue from deleting that record's Snipe-IT asset.
- `Preview` writes candidates that pass the current deletion gates as `Operation=Delete` and `ChangeReasons=MissingFromAtera`, but it never calls DELETE.
- A real deletion uses Snipe-IT hardware soft-delete. One delete failure is recorded but does not prevent other already-approved delete candidates from being attempted.

In practical terms: duplicate or matching problems block the affected records, healthy records may continue syncing, but all stale-asset deletion is disabled for that run. Deletion resumes only after an administrator fixes the conflicts and runs Preview / Sync again.

## Snipe-IT data currently managed

| Object | Current behavior |
| --- | --- |
| Hardware Assets | Create, update, no-op detection, and soft-delete of stale `ATERA-` assets |
| Companies | Reuse exact name or alias; optionally create missing Companies |
| Categories | Find the Default Category and create it as an Asset category when missing |
| Models | Find and optionally create Models; update Category and Fieldset on existing Models |
| Manufacturers | Look up and bind only; never automatically create; record a warning and allow an unbound Model when missing |
| Custom Fields / Fieldsets | Look up only; never automatically create the MAC custom field or Fieldset |

## Known issues / limitations

- Only Windows x64 is supported. The current formal validation targets are Windows 11 x64 and Windows Server 2022 x64.
- The MSI is currently unsigned. Windows may display an Unknown publisher or SmartScreen warning. Obtain the MSI from a trusted source and verify its bundled SHA-256 before installation.
- The Atera API key, Snipe-IT token, and SMTP password are currently stored as plaintext in `%ProgramData%\AteraSnipeSync\appsettings.local.json`. Password fields are masked only in the GUI; DPAPI, Credential Manager, and secret-vault storage are not implemented. Deploy only on a trusted machine, and never copy, print, log, or commit this file.
- The current Atera source reads only the agents endpoint. Company association comes from customer fields in the agent payload; it does not independently synchronize the full Atera customer directory.
- An individual malformed Atera record, or one missing a required identity, is skipped with a warning instead of failing the entire pull. If that device was previously synchronized, it may become a `MissingFromAtera` candidate. Do not run a real sync when Atera malformed-record warnings are present; inspect Preview and repair the source data first.
- All imported devices currently map to one `Default category`. Per-device-type Category mapping is not yet supported.
- Manufacturers are not created automatically. Pre-create each Manufacturer in Snipe-IT or configure an alias. Otherwise, the Model is processed without a manufacturer binding and a warning is recorded.
- `Normalize from categories` scans and may modify existing Snipe-IT Models, not only Models used by the current Atera agents. Incorrect settings can expand the change scope significantly.
- `Ignored device types` removes those devices from the current inventory; it does not mean “keep but do not update.” Previously synchronized `ATERA-` assets of those types may become `MissingFromAtera` candidates.
- A successful but empty Atera inventory treats every `ATERA-` asset as stale. Snapshot and failure gates protect this behavior, but Preview remains mandatory.
- Deletion is Snipe-IT soft-delete. The tool has no restore, archive, or permanent-purge UI.
- The current `Sync Now` confirmation dialog mentions create/update, but a real run may also soft-delete. Treat the Preview CSV as authoritative.
- The Worker uses one global non-overlap gate. Preview, Sync Now, Connection Test, and scheduled sync cannot run concurrently; new work is not queued while the Worker is busy.
- Schedule state is persisted. Multiple overdue occurrences while the service is stopped are coalesced into at most one catch-up run instead of replaying every missed occurrence.
- The Health Check / Snipe-IT duplicate-data repair module remains on the roadmap. The current importer reports ambiguity and blocks affected records, but it does not merge, rename, or repair existing duplicate references.
- History is retained for 90 days with a maximum of 500 files. Tray daily logs are retained for 30 days. These retention settings are not configurable in the Config GUI.
- Preview directories currently have no automatic retention cleanup and must be removed according to the organization's data-retention policy.

## Prerequisites

Prepare the following before installation:

1. A Windows x64 machine and a local administrator account that can install an MSI.
2. An Atera API key with at least permission to read agents.
3. A Snipe-IT HTTPS URL and API token. The token must be able to read the Hardware, Companies, Categories, Models, Manufacturers, and Fieldsets used by this tool. A real sync also requires create/update permissions for enabled objects and delete permission for stale Hardware.
4. A valid Snipe-IT Asset Status ID.
5. Optional: a Snipe-IT custom field for MAC storage and a Fieldset that contains that field.
6. Optional: an SMTP relay, Teams Workflow URL, or another HTTPS webhook.

Back up Snipe-IT first, and complete at least one Preview and one small real sync in a test environment before production rollout.

## Installation

### 1. Verify the installer

The release directory contains:

```text
AteraSnipeSync-1.0.0-win-x64.msi
AteraSnipeSync-1.0.0-win-x64.msi.sha256
release-manifest.json
```

Calculate the hash in PowerShell and compare it with `.sha256` or `release-manifest.json`:

```powershell
Get-FileHash -LiteralPath .\AteraSnipeSync-1.0.0-win-x64.msi -Algorithm SHA256
Get-Content -LiteralPath .\AteraSnipeSync-1.0.0-win-x64.msi.sha256
```

### 2. Install the MSI

Run from an elevated PowerShell or Windows Terminal:

```powershell
msiexec.exe /i .\AteraSnipeSync-1.0.0-win-x64.msi /norestart
```

After installation:

- Application files are installed in `C:\Program Files\AteraSnipeSync`.
- Windows Service `AteraSnipeItAutoSync` is installed and started as LocalSystem with Automatic startup.
- `Atera Snipe-IT Auto Sync` appears in the all-users Start Menu.
- The Tray App is registered to start when a user signs in.
- Local application data is stored in `C:\ProgramData\AteraSnipeSync`.

If the Tray icon does not appear immediately, launch `Atera Snipe-IT Auto Sync` from the Start Menu. Exiting the Tray App does not stop the background Worker Service.

## First-time setup and use

Recommended rollout sequence:

1. Open the Dashboard from the Tray icon or Start Menu.
2. Select `Configuration` and complete all four tabs described below.
3. On `API & Credentials`, click `Test Connections` and confirm that both Atera and Snipe-IT show Success.
4. Save the configuration, but leave Schedule disabled.
5. Return to the Dashboard and click `Preview`.
6. Open `C:\ProgramData\AteraSnipeSync\Preflight\<run-id>` and inspect all four CSV files.
7. Resolve every `Blocked` row, duplicate identity, ambiguous match, and unexpected Add / Modify / Delete.
8. Confirm that every automatically managed Asset Tag has the expected `ATERA-...` form, and review every `MissingFromAtera` row carefully.
9. Return to the Dashboard, click `Sync Now`, and confirm execution.
10. Review Dashboard counts, Logs, History, and the resulting Snipe-IT data.
11. Enable Schedule and Notifications only after the result is stable.

### Preview output

Each Preview creates a new run directory containing:

| File | Contents |
| --- | --- |
| `snipeit-assets-plan.csv` | Asset Add / Modify / Blocked / Delete operations, matched IDs, conflict evidence, and change reasons |
| `snipeit-companies-plan.csv` | Planned creation of missing Companies |
| `snipeit-categories-plan.csv` | Planned creation of missing Asset Categories |
| `snipeit-models-plan.csv` | Model creation, Category normalization, and Fieldset update plans |

Externally supplied values are neutralized against spreadsheet-formula execution, but these CSV files may still contain customer and device information. Handle them as sensitive operational data.

### Dashboard actions

| Action | Behavior |
| --- | --- |
| `Preview` | Performs complete reads and planning and writes CSV files, but does not modify Snipe-IT |
| `Sync Now` | Runs a real sync that may create, update, and soft-delete |
| `Cancel` | Requests cancellation of the active Worker operation; completed writes are not rolled back |
| `Restart Service` | Uses UAC elevation to restart or repair the Worker Service state |
| `Open Log Folder` | Opens the controlled local application-data directory |
| `Configuration` | Edits and saves the shared Tray / Worker configuration |

## Config GUI tutorial

### API & Credentials

| Field | Required | Configuration |
| --- | --- | --- |
| `Atera API base URL` | Yes | Keep `https://app.atera.com/api/v3/`. The security validator accepts only the official `app.atera.com` HTTPS host. |
| `Atera API key` | Yes | Paste the Atera API key. The field is masked in the GUI but stored as plaintext in the local JSON file. |
| `Snipe-IT API base URL` | Yes | Enter the complete API v1 base URL, for example `https://assets.example.com/api/v1`. Non-loopback addresses must use HTTPS, and the path must end in `/api/v1`. |
| `Snipe-IT API token` | Yes | Paste the Snipe-IT Bearer token. Use a service-account token with the minimum permissions required for the enabled operations. |

`Test Connections` first validates and saves the complete form, asks the Worker to reload, and then performs bounded read-only probes. Atera reads at most one page, and Snipe-IT requests one hardware row. The test verifies basic URLs, authentication, and read permission, but it does not prove create/update/delete permission or validate every reference endpoint.

### Mapping & Import

#### Company aliases

Enter one `source=target` mapping per line:

```text
Vue IT Incorporated=Vue IT Inc.
Contoso Canada Ltd.=Contoso
```

Rules:

- Each line must contain exactly one `=` and cannot use `=>`.
- Neither the key nor the value may be blank.
- Matching is case-insensitive and normalizes repeated whitespace and dash-like characters.
- The importer checks the original Atera company name first. It tries the alias only when the original name is absent from Snipe-IT, preventing an existing exact name from being redirected incorrectly.
- The fixed fallback for a missing Atera customer name is `Unknown Company`.

#### Manufacturer aliases

The format is the same as Company aliases:

```text
Dell Inc.=Dell
LENOVO=Lenovo
```

The importer checks the source manufacturer first and the alias second. If neither exists, it does not create a Manufacturer; it records a warning and allows the Model to proceed without a manufacturer binding.

#### Other Mapping / Import fields

| Field | Default | Behavior |
| --- | --- | --- |
| `Default category` | `Computer` | Target Asset Category for every imported device and target of Model category normalization. The Category is created when missing. |
| `Normalize from categories (;)` | `Server; Laptop; Desktop` | Separate values with semicolons, commas, or new lines. Existing Snipe-IT Models in these categories are planned for movement to `Default category`. At least one value is required. To disable cross-category normalization, use the same single value as `Default category`, for example `Computer`. |
| `Ignored device types (;)` | Blank | Completely excludes matching Atera device types. Matching is trimmed, exact, and case-insensitive. Previously synchronized `ATERA-` assets of these types may become deletion candidates. |
| `Default status ID` | `2` | Positive integer written when Hardware is created or updated. A different status on an existing asset produces a `Status` modification. Confirm the actual ID in Snipe-IT Status Labels instead of guessing from the label name. |
| `MAC custom field DB column` | Blank | Database column of the Snipe-IT custom field, usually similar to `_snipeit_mac_address_5`. Copy the real DB column, not the display label. |
| `MAC fieldset name` | Blank | Exact name of the Snipe-IT Fieldset containing the MAC field. It must be filled together with the DB column or left blank together. |
| `Ignored MAC addresses (;)` | Blank | Excludes common virtual, shared, or meaningless MAC addresses. Colon, dash, dot, and unseparated 12-digit hexadecimal forms are accepted and saved as `AA:BB:CC:DD:EE:FF`. |
| `Name match threshold` | `0.92` | Must be in `(0, 1]`. Used only when Asset Tag, MAC, and reliable serial do not match. Higher values are more conservative, and fallback matching also requires the same Company, Category, and Model context. |
| `Create missing companies` | On | Creates a missing Company when enabled. When disabled, affected assets are Blocked. |
| `Create missing models` | Off | Creates a missing Model when enabled. When disabled, affected assets are Blocked. Keep it off during initial rollout, organize Models through Preview, and enable it only when intended. |

MAC behavior:

- Leaving both MAC fields blank disables MAC matching and MAC payload writes and produces one informational warning.
- When enabled, the Fieldset must exist and contain the configured custom field. Otherwise, planning fails.
- When a device has multiple MAC addresses, the payload currently manages the first valid, non-ignored MAC. All valid MACs remain visible in the Preview audit column.
- Ignored MAC addresses do not participate in duplicate detection, existing-asset matching, or payload selection.
- When a Fieldset is configured, the importer also assigns it to Models already in the target Category and Models normalized into the target Category. Review `snipeit-models-plan.csv`.

Duplicate identity behavior:

- Duplicate source IDs, Asset Tags, reliable serials, or non-ignored MAC addresses block the conflicting records.
- Known placeholder serial values are treated as unreliable identities.
- When multiple `Virtual Machine` Models share a serial, that serial remains audit-only and is excluded from matching and payloads, avoiding a common virtual-machine false conflict.

### Schedule

| Field | Default | Behavior |
| --- | --- | --- |
| `Frequency` | `Daily` | Select `Daily`, `Weekly`, or `Monthly`. |
| `Schedule enabled` | Off | When enabled, the Worker performs real syncs according to the configured rule, not Previews. |
| `Windows time zone ID` | Current Windows time zone | Must be a Windows time zone ID recognized by the host, such as `Mountain Standard Time`. |
| `Run times (;, HH:mm)` | `02:00` | Uses 24-hour `HH:mm` format. Configure multiple unique times with semicolons, commas, or new lines, for example `02:00; 14:00`. |
| `Weekly days (;)` | `Monday` | Visible for Weekly only. Use English `DayOfWeek` names, such as `Monday; Wednesday; Friday`. |
| `Monthly days (;)` | `1` | Visible for Monthly only. Values must be `1` through `31`; a day that does not exist in a particular month is skipped. |
| `Also run on the last day of month` | Off | Visible for Monthly only. It can be used by itself or together with specific month days. |

All Worker operations are non-overlapping. If a scheduled time arrives while Preview, Sync, Connection Test, or another scheduled run is active, that scheduled operation is not run concurrently or queued.

After `Save changes`, the Dashboard asks the Worker to reload the schedule. Confirm that the Dashboard shows `Schedule enabled` and the expected `Next run`; writing the JSON file alone does not prove that the Worker applied it successfully.

### Notifications

#### Master switch and events

| Field | Behavior |
| --- | --- |
| `Notifications enabled` | Master switch. No normal run notification is sent while it is off. |
| `Sync completed` | Sends completed events for scheduled sync, manual Sync Now, and manual Preview. A completed run may still contain record-level warnings or failures, so inspect counts and History. |
| `Sync failed` | Sends events for incomplete or fatal scheduled/manual/preview runs. |

Normal run notifications require the master switch, at least one selected event, and at least one completely configured channel.

#### Email / SMTP

| Field | Configuration |
| --- | --- |
| `SMTP host` | SMTP server hostname; required when Email is configured. |
| `SMTP port` | `1` through `65535`; default `587`. |
| `Use TLS/SSL for SMTP` | Enabled by default and mapped to the SMTP client's SSL/TLS option. |
| `SMTP username` / `SMTP password` | Fill both or leave both blank. When blank, the service uses anonymous/IP-based relay and never uses the LocalSystem Windows credentials. |
| `Email from` | One valid sender address; required when Email is configured. |
| `Email to` | One or more valid recipients separated by semicolons, commas, or new lines. |

#### Webhook

| Field | Configuration |
| --- | --- |
| `Webhook format` | `Teams Workflow (Adaptive Card)` sends a message with an Adaptive Card attachment for Teams Workflow. `Generic JSON` sends a plain contract to another HTTPS endpoint. |
| `Webhook URL` | Must be an absolute HTTPS URL. Treat the URL as a secret and do not include it in tickets, logs, or screenshots. |

The top-level Generic JSON fields are `eventType`, `severity`, `subject`, `message`, numeric `deleted`, and `occurredAtUtc`.

`Test Notifications` first saves the current configuration and then sends one real test message through each completely configured Email / Webhook channel. `Accepted` means only that the SMTP or webhook endpoint accepted the request; it does not confirm final delivery or downstream workflow processing. Test sends do not depend on the normal notification master switch or event selections.

### Save changes and Cancel

- `Save changes` validates the complete configuration and atomically writes the shared JSON file. Any invalid required field, URL, MAC, schedule, alias, SMTP, or webhook setting prevents the save.
- `Cancel` returns to the Dashboard without saving current edits, reloading the Worker, or calling an external API.
- The test buttons also save the complete current form. They are not temporary, non-persistent tests.

## Local files and retention

| Path | Contents | Default retention |
| --- | --- | --- |
| `C:\ProgramData\AteraSnipeSync\appsettings.local.json` | Complete shared configuration and plaintext credentials | Until explicitly removed or deleted during uninstall |
| `C:\ProgramData\AteraSnipeSync\schedule-state.json` | Next/last scheduled occurrence and rule fingerprint; no secrets | Until explicitly removed or deleted during uninstall |
| `C:\ProgramData\AteraSnipeSync\Logs` | Tray daily operation logs | 30 days |
| `C:\ProgramData\AteraSnipeSync\History` | Structured JSON for each completed run | 90 days and no more than 500 files |
| `C:\ProgramData\AteraSnipeSync\Preflight\<run-id>` | Preview CSV files | No automatic cleanup currently |

Logs, History, and IPC summaries avoid API tokens, raw HTTP payloads, and complete Atera inventories. They still contain limited customer and asset identity information needed for troubleshooting and must be managed according to the organization's data-protection requirements.

## Uninstallation

### Interactive uninstall

Uninstall `Atera Snipe-IT Auto Sync` from Windows Installed apps / Programs and Features, or run:

```powershell
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /norestart
```

The uninstall dialog asks whether to delete:

```text
%ProgramData%\AteraSnipeSync
```

The option is clear by default, so configuration, credentials, logs, history, Preview files, and schedule state are preserved for a later reinstall. Selecting the option permanently removes the entire local data directory.

### Silent uninstall

Preserve all local data:

```powershell
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /qn /norestart
```

Permanently remove the complete local data directory as well:

```powershell
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /qn /norestart REMOVELOCALDATA=1
```

`REMOVELOCALDATA=1` applies only to a true uninstall. Major upgrades and repairs preserve local data even when this property is supplied.

> [!CAUTION]
> Deleting `%ProgramData%\AteraSnipeSync` removes plaintext credentials, all logs, sync history, Preview CSV files, and schedule state. Back up required audit records according to organizational policy before deletion.

## Troubleshooting

### Service is Running but Worker is Offline

1. Click `Restart Service` on the Dashboard and accept UAC.
2. Inspect `C:\ProgramData\AteraSnipeSync\Logs`.
3. Confirm that the configuration file is valid JSON and that LocalSystem can read the ProgramData directory.
4. Confirm that Worker and Tray versions in the installation directory match.

### Configuration was saved but Schedule is invalid

- Inspect the Dashboard Schedule status and error instead of relying only on the save confirmation.
- Confirm that the Windows time zone ID exists, run times use `HH:mm`, Weekly has at least one day, and Monthly has at least one day or the last-day option.
- Save again and confirm that Worker reload succeeds. Restart the Service if necessary.

### Preview contains many Blocked rows

- Inspect `FailureCode`, `ConflictingFields`, `ConflictingValue`, and `ConflictingAssets`.
- Repair duplicate Agent ID / serial / MAC data in Atera, or add a known shared MAC to `Ignored MAC addresses`.
- Clean up duplicate Asset Tag, serial, Model, or reference records in Snipe-IT. The tool does not automatically choose or merge ambiguous records.
- For missing Companies or Models, check aliases and the `Create missing ...` switches.

### Preview contains no Delete operations

Any validation, reference, matching, or duplicate-target failure activates the fail-closed gate and clears every deletion plan. Resolve all Blocked/failure records and run Preview again.

## Development and verification

.NET 10 SDK is required. Restore, build, and run the offline automated test suite:

```powershell
dotnet restore .\AteraSnipeSync.sln
dotnet build .\AteraSnipeSync.sln --no-restore
dotnet test .\AteraSnipeSync.sln --no-build --no-restore
```

Automated tests use mocked HTTP handlers, fake clients, and local fixtures. They never call the real Atera or Snipe-IT APIs.

Development verification may build a release from a dirty worktree:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1 -AllowDirty
```

The final artifact must be built from a clean commit without `-AllowDirty`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1
```

Main projects:

- `src/AteraSnipeSync.Core`: Atera pull, mapping, Snipe-IT import, scheduling, status, and notification logic.
- `src/AteraSnipeSync.WorkerService`: Windows Service, scheduler, runtime composition, and local IPC.
- `src/AteraSnipeSync.TrayApp`: Dashboard, Configuration GUI, and service-maintenance UI.
- `tests/AteraSnipeSync.Tests`: Offline unit, contract, and integration-style tests.
- `installer/AteraSnipeSync.Installer`: WiX 7 MSI installer.
- `docs`: Module responsibilities, technical specifications, test guides, flow diagrams, and roadmap.

Never add real API keys, tokens, SMTP passwords, webhook URLs, or a production `appsettings.local.json` to the repository.
