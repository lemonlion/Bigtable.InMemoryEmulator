# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
