# Project Roadmap

This document records future work that is intentionally outside the current implementation scope. A backlog item must still follow the repository module workflow before production code is added.

## Backlog

### Health Check Module - Snipe-IT Data Integrity

Status: Backlog  
Target timing: Design before unattended/scheduled sync is treated as production-ready  
Operating mode: Read-only; manual, pre-sync, or scheduled health check

#### Goal

Detect suspicious Snipe-IT data conditions that are commonly caused by manual administration mistakes or gradual data drift. Report the exact affected records before they create ambiguous matching, unexpected reference creation, or a real-sync block.

The duplicate `Dell pro 14` model records that exposed the model-lookup ambiguity are the first concrete use case for this module.

#### Initial detection scope

- Models with the same normalized name and category but different Snipe-IT ids.
- Categories with the same normalized name and asset type but different ids.
- Companies and manufacturers with duplicate normalized names.
- Custom fields with duplicate display names, unexpected database field names, or ambiguous mappings, including the MAC address field.
- Models with missing, invalid, or suspicious category/manufacturer relationships.
- Near-duplicate reference data caused by case, surrounding whitespace, repeated spaces, or punctuation differences.
- Optional source-batch checks for repeated asset tags, serial numbers, or normalized MAC addresses, while preserving the existing import-time identity checks.

Legitimate same-name records must not be silently classified as errors. Each rule needs a deterministic key, documented exceptions, and enough record context for an operator to decide whether the records are truly duplicates.

#### Proposed result contract

Each finding should include:

- Stable issue code and severity.
- Resource type, normalized comparison key, and all affected record ids.
- Relevant evidence such as names, category ids, manufacturer ids, and differing fields.
- A plain-language risk explanation and recommended manual verification steps.
- Whether the finding is informational, should warn before sync, or is severe enough to block only the affected identity/reference key.

Candidate issue codes include `DuplicateModelKey`, `DuplicateCategoryKey`, `DuplicateCompanyKey`, `DuplicateManufacturerKey`, `DuplicateCustomFieldName`, `InvalidReferenceRelationship`, `SuspiciousNearDuplicate`, and `DuplicateSourceIdentity`.

Results should be available as a structured run result and operator-readable report, with CSV/JSON export and a Tray App summary considered during design.

#### Safety and responsibility boundaries

- The module must use read-only API operations and must not merge, delete, rename, or repair Snipe-IT records automatically.
- It must report ambiguity instead of selecting an arbitrary record.
- A finding for one normalized key must not invalidate unrelated healthy records or the entire scan.
- Automated tests must use mocked HTTP handlers or sanitized fixtures and must never call a real Atera or Snipe-IT API.
- Before implementation, the official Snipe-IT API documentation must be consulted for every resource and response shape used by the module.

#### Open design decisions

- Severity rules and whether pre-sync integration defaults to Off, Warn, or Block Critical.
- Exact normalization rules for each Snipe-IT resource type.
- Near-duplicate matching threshold and false-positive handling.
- Supported exceptions for intentionally duplicated names.
- Scan frequency, pagination/caching strategy, and report retention.

#### Backlog exit criteria

Before implementation begins:

1. Create and approve `docs/module-plans/<health-check>-功能职责.md`.
2. Create and approve `docs/technical-specs/<health-check>-技术规格.md`.
3. Define the read-only API inventory, issue taxonomy, severity policy, and deterministic normalization rules.
4. Add production code and mocked tests only after both documents exist.
5. Add the unit test guide and update `progress.md` in the same implementation work session.
