# Bigtable.InMemoryEmulator

[![NuGet](https://img.shields.io/nuget/v/Bigtable.InMemoryEmulator.svg)](https://www.nuget.org/packages/Bigtable.InMemoryEmulator)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Bigtable.InMemoryEmulator.svg)](https://www.nuget.org/packages/Bigtable.InMemoryEmulator)
[![CI](https://github.com/lemonlion/Bigtable.InMemoryEmulator/actions/workflows/ci.yml/badge.svg)](https://github.com/lemonlion/Bigtable.InMemoryEmulator/actions/workflows/ci.yml)

A fully featured, in-process fake for the Google Cloud Bigtable SDK for .NET — purpose-built for fast, reliable component and integration testing. Runs in memory on the fly for the lifetime of your test run (although persistence between runs is available as a feature).

Has full support for all Bigtable mutations (SetCell, Delete, CheckAndMutateRow, ReadModifyWriteRow), RowFilter queries, GoogleSQL querying via `ExecuteQuery`, change feed via `ReadChangeStream`, and GC rules.

Works by hosting an in-process gRPC server implementing `Bigtable.BigtableBase` — a real `BigtableClient` is created with a `GrpcChannel` pointing at an ASP.NET Core `TestServer`. Your production code stays completely untouched.

## Usage

### Dependency Injection

In your `ConfigureTestServices()` method in your `WebApplicationFactory()`:

```csharp
serviceCollection.UseInMemoryBigtable(options =>
{
    options.AddTable("my-table", new[] { "cf1", "cf2" });
});
```

Both the `BigtableClient` and optionally `BigtableTableAdminClient` registrations are replaced — full SDK fidelity, `ReadRows` streaming works natively, no production code changes needed.

See **[Setup Guide](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Setup-Guide)** for all registration patterns and **[Choosing Your Approach](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Choosing-Your-Approach)** for a comparison of the available approaches.

### Direct Instantiation

Single table:

```csharp
// Single table — one-liner
using var bigtable = InMemoryBigtable.Create("my-table", new[] { "cf1", "cf2" });

// Use the real SDK — all calls are intercepted in-memory via gRPC
await bigtable.Client.MutateRowAsync(
    bigtable.TableName("my-table"),
    "row1",
    Mutations.SetCell("cf1", "name", "Alice"));
```

Multi-table:

```csharp
using var bigtable = InMemoryBigtable.Builder()
    .AddTable("users", new[] { "profile", "activity" })
    .AddTable("events", new[] { "data" }, gc => gc.MaxVersions("data", 5))
    .ProjectId("my-project")
    .InstanceId("my-instance")
    .Build();

var users = bigtable.TableName("users");
var events = bigtable.TableName("events");
```

Test setup and fault injection:

```csharp
// Seed data
await bigtable.Client.MutateRowAsync(table, "row1",
    Mutations.SetCell("cf1", "col", "value"));

// Inject faults (UNAVAILABLE, DEADLINE_EXCEEDED, etc.)
bigtable.SetFaultInjector(rpc => rpc == "ReadRows" ? Status.Unavailable : null);
```

See **[Getting Started](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Getting-Started)** for a full walkthrough and **[API Reference](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/API-Reference)** for all available methods.

## Motivation

Designed for super fast feedback from your integration/component tests in a local or CI environment, to avoid relying completely on the official Go emulator or real GCP Bigtable or inaccurate high-level abstractions.

| Traditional Approach | Problem |
|----------|---------|
| **[Go Bigtable Emulator](https://cloud.google.com/bigtable/docs/emulator)** | Missing features (no GoogleSQL, no ReadChangeStream), requires Docker, slow startup, poor diagnostics |
| **Real GCP Bigtable** | Slower, costly, requires network and authentication, shared state between test runs |
| **Repository Abstraction Layer** | Fragile, doesn't test query logic, misses serialization bugs |

Recommendation is to use **Bigtable.InMemoryEmulator** for integration/component testing locally and in CI for quick feedback and iteration, while still having the integration/component tests *additionally* running in CI against the Go emulator for (slower) parity validation.

See the **[Feature Comparison](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Feature-Comparison)** for a detailed side-by-side breakdown.

## Features

- **Full CRUD mutations** — SetCell, DeleteFromColumn, DeleteFromFamily, DeleteFromRow with proper gRPC status codes
- **CheckAndMutateRow** — atomic compare-and-swap with RowFilter predicates
- **ReadModifyWriteRow** — atomic increment and append operations
- **ReadRows** — full streaming with RowSet, RowRange, row key filtering, reverse scans, request stats
- **22+ RowFilter types** — RowKeyRegex, FamilyNameRegex, ColumnQualifierRegex, ValueRegex, TimestampRange, CellsPerColumnLimit, Chain, Interleave, Condition, ApplyLabelTransformer, Sink, and more — see **[RowFilter Reference](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/RowFilter-Reference)**
- **GoogleSQL query engine** — SELECT, WHERE, GROUP BY, ORDER BY, LIMIT, pipe syntax, MAP subscript, CAST, window functions, 100+ built-in functions — see **[GoogleSQL Queries](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/GoogleSQL-Queries)**
- **Change feed** — `ReadChangeStream` with DataChange, Heartbeat, CloseStream messages, continuation tokens, and resume semantics — see **[Change Feed](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Change-Feed)**
- **GC rules** — MaxVersions, MaxAge, Intersection, Union with arbitrary nesting, eager-on-write and read-time enforcement
- **Aggregation cells** — AddToCell/MergeToCell with Sum, Min, Max aggregators
- **MutateRows (batch)** — per-entry status codes, atomic per-row
- **State persistence** — export/import table state as JSON; automatic save/restore between test runs via `StatePersistenceDirectory` — see **[State Persistence](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/State-Persistence)**
- **Fault injection** — simulate UNAVAILABLE, DEADLINE_EXCEEDED, RESOURCE_EXHAUSTED per-RPC — see **[Fault Injection](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Fault-Injection)**
- **Dependency Injection integration** — `UseInMemoryBigtable()` extension methods for `IServiceCollection` — see **[Setup Guide](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Setup-Guide)**
- **gRPC-level interception** — in-process TestServer for zero-code-change integration
- **Table admin** — CreateTable, DeleteTable, ModifyColumnFamilies, ListTables
- **Request validation** — all Bigtable size limits and validation rules enforced with correct gRPC status codes
- **Concurrency** — thread-safe with documented locking hierarchy for deadlock-freedom

For the full feature list see [Features](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Features). For a side-by-side comparison with the Go emulator see [Feature Comparison](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Feature-Comparison). For behavioral differences from real GCP Bigtable see [Known Limitations](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Known-Limitations).

## NuGet Packages

| Package | Description | NuGet |
|---|---|---|
| `Bigtable.InMemoryEmulator` | Core in-memory emulator | [![NuGet Version](https://img.shields.io/nuget/v/Bigtable.InMemoryEmulator)](https://www.nuget.org/packages/Bigtable.InMemoryEmulator) |

## Documentation

Full documentation is available on the **[Wiki](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki)**.

| Guide | Description |
|---|---|
| **[Getting Started](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Getting-Started)** | Quick start walkthrough |
| **[Choosing Your Approach](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Choosing-Your-Approach)** | Layer 1 vs Layer 3 comparison |
| **[Setup Guide](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Setup-Guide)** | All setup and registration patterns |
| **[RowFilter Reference](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/RowFilter-Reference)** | All 22+ filter types with examples |
| **[GoogleSQL Queries](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/GoogleSQL-Queries)** | SQL engine capabilities and functions |
| **[Seeding Data](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Seeding-Data)** | Populating tables for tests |
| **[State Persistence](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/State-Persistence)** | Export/import and automatic persistence |
| **[API Reference](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/API-Reference)** | Full API surface documentation |
| **[Troubleshooting](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Troubleshooting)** | Common issues and fixes |
| **[Known Limitations](https://github.com/lemonlion/Bigtable.InMemoryEmulator/wiki/Known-Limitations)** | Behavioral differences from real GCP Bigtable |

## Emulator Parity Validation

The test suite includes infrastructure to validate that the in-memory implementation produces identical results to the Go Bigtable emulator and real GCP Bigtable. Integration tests use the same SDK gRPC pipeline as a real emulator, making comparison meaningful.

### CI

- The `emulator-parity.yml` workflow runs weekly (Monday 6am UTC) or on manual trigger, executing integration tests against both in-memory and Go emulator backends and producing a parity report.
- The `gcp-parity.yml` workflow runs weekly against a real GCP Bigtable instance for features the Go emulator doesn't support (GoogleSQL, ReadChangeStream).

## Dependencies

| Package | Purpose |
|---------|---------|
| [Google.Cloud.Bigtable.V2](https://www.nuget.org/packages/Google.Cloud.Bigtable.V2) | Google Cloud Bigtable SDK |
| [Google.Cloud.Bigtable.Admin.V2](https://www.nuget.org/packages/Google.Cloud.Bigtable.Admin.V2) | Table/family admin types |
| [Google.Cloud.Bigtable.Common.V2](https://www.nuget.org/packages/Google.Cloud.Bigtable.Common.V2) | Shared types (BigtableByteString, BigtableVersion) |
| [Grpc.Net.Client](https://www.nuget.org/packages/Grpc.Net.Client) | gRPC channel for in-process TestServer |
| [Superpower](https://www.nuget.org/packages/Superpower) | Parser combinators for GoogleSQL engine |

## License

[MIT License](LICENSE)
