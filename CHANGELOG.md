# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.6] - 2026-04-25

### Added
- `ResourceNameParser`: Superpower-based parser for Bigtable resource names, replacing duplicated hand-rolled `Split('/')` logic in both gRPC services
- `SdkVersionDriftDetector`: detects when the Google.Cloud.Bigtable.V2 SDK version drifts from the tested version (3.15.0), warning about potential breaking changes
- `InMemoryTableOptions`: per-table configuration class (GC rules, `OnCreated` callback) — concept mapping equivalent of CosmosDB's `InMemoryContainerOptions`
- `AddTable` overload accepting `Action<InMemoryTableOptions>` on both `InMemoryBigtableBuilder` and `InMemoryBigtableOptions` (DI)
- `cleanup-orphan-instances.yml`: GitHub Actions workflow for daily cleanup of stale GCP Bigtable test instances

### Changed
- `ExtractTableName` in `BigtableGrpcService` and `BigtableTableAdminGrpcService` now delegates to shared `ResourceNameParser`
- `AddTable` with GC rules now uses `Action<InMemoryTableOptions>` instead of `Action<GcRuleBuilder>` (breaking: `gc => gc.MaxVersions(...)` → `opts => opts.MaxVersions(...)`)

## [0.1.5] - 2026-04-25

### Added
- `ITableTestSetup.GcRules` property: exposes GC rules per column family for test inspection
- `ITableTestSetup.StateFilePath` property: exposes the auto-persistence file path
- ReadChangeStream GARBAGE_COLLECTION entries: GC evictions (MaxVersions/MaxAge) now emit change stream entries with Type.GarbageCollection
- ReadChangeStream partition validation: continuation token partitions are validated against the full-table partition (INVALID_ARGUMENT on mismatch)
- PrepareQuery RPC: documented as deferred — types not yet available in Google.Cloud.Bigtable.V2 NuGet v3.15.0

### Changed
- `InMemoryTableTestSetup` constructor now accepts optional `stateFilePath` parameter

## [0.1.3] - 2026-04-24

### Added
- ReadChangeStream integration tests (data change, heartbeat, continuation token resume, end_time)
- Error validation integration tests (nonexistent table/family, empty row key, duplicate table)
- Concurrency integration tests (parallel MutateRows, CheckAndMutateRow atomicity)
- GC rules integration tests (MaxVersions, MaxAge expiration at read time)
- GoogleSQL additional integration tests (pipe syntax, aggregate functions)
- Fault injection and diagnostics integration tests (FaultInjector, RpcLog, QueryLog recording)

### Fixed
- RPC logging now records ALL gRPC calls (previously only recorded faulted calls)
- QueryLog now correctly records ExecuteQuery SQL queries
- FaultInjection tests use targeted method matching (avoid ReadRows which ReadRowAsync uses internally)

## [0.1.2] - 2026-04-24

### Added
- 8 new integration tests for aggregation, Admin API aggregate family support

### Fixed
- Admin API aggregate family parsing: correctly reads `Sum`, `Min`, `Max` properties (not underscored)
- GC read-time filtering: cells are now filtered by GC rules (MaxVersions, MaxAge) at read time
- Reversed scan integration test: uses `BigtableServiceApiClient` (SDK `BigtableClient.ReadRows()` lacks `reversed` parameter)
- Aggregation integration tests: verify AddToCell/MergeToCell via the gRPC pipeline

## [0.1.1] - 2026-04-23

### Added
- GoogleSQL query engine: tokenizer, parser (Superpower), executor, 125+ built-in functions
- ExecuteQuery gRPC endpoint with server-streaming response (ResultSetMetadata + ProtoRowsBatch)
- GoogleSQL support: SELECT, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT, OFFSET, DISTINCT
- GoogleSQL expressions: CAST, SAFE_CAST, CASE/WHEN/THEN/ELSE/END, map subscript, member access
- GoogleSQL functions: string (CONCAT, LENGTH, LOWER, UPPER, TRIM, SUBSTR, REPLACE, etc.), math (ABS, CEIL, FLOOR, ROUND, SQRT, POWER, trig), conversion (TO_HEX, FROM_HEX, TO_BASE64, FROM_BASE64), conditional (IF, IFNULL, COALESCE)
- GoogleSQL aggregate functions: COUNT, SUM, AVG, MIN, MAX
- GoogleSQL parameterized queries (@param syntax)
- Admin API: CreateTable, GetTable, DeleteTable, ListTables, ModifyColumnFamilies
- ReadChangeStream gRPC endpoint: DataChange, Heartbeat, CloseStream messages
- GenerateInitialChangeStreamPartitions endpoint
- Change feed continuation tokens and resume support
- State persistence: ExportState/ImportState (JSON), ExportStateToFile/ImportStateFromFile
- InMemoryBigtable public API with Builder pattern
- InMemoryBigtableServer with ASP.NET Core TestServer for in-process gRPC
- Aggregation cells: AddToCell/MergeToCell mutations, Sum/Min/Max aggregators
- RowFilter engine: 22 filter types with Chain, Interleave, Condition composites
- Full mutation support: SetCell, DeleteFromColumn, DeleteFromFamily, DeleteFromRow
- CheckAndMutateRow and ReadModifyWriteRow (increment/append)
- MutateRows batch support with per-entry status
- ReadRows with RowSet, RowRange, row filters, rows_limit, reversed scans
- SampleRowKeys and PingAndWarm endpoints
- ETag-based cell versioning with timestamp support
- GC rules: MaxVersions, MaxAge per column family
- Request validation with proper gRPC status codes (NOT_FOUND, INVALID_ARGUMENT, etc.)
- 186 tests across unit and integration test projects (net8.0 + net10.0)

## [0.1.0] - 2026-04-23

### Added
- Initial project scaffolding
- Solution structure with main library, shared tests, unit tests, integration tests, and performance tests
- Build infrastructure: Directory.Build.props, global.json, .editorconfig
- CI workflows: build-and-test, emulator-parity, GCP parity, release, rebalance-shards, CodeQL
- PowerShell scripts: run-tests, compare-trx, validate-parity, start-emulator, Analyze-TestTimings
- AGENTS.md with TDD workflow, behavioral source requirements (Bigtable API references), test classification rules
- Wiki scaffolding with 19 pages
