# TradingHub

## Project Overview

TradingHub is a modular C#/.NET trading system in an early-stage state. The codebase is organized around separate projects for broker integration, market-data access, strategy evaluation, trade management, persistence, and utilities.

At present, the implemented core is the broker metadata and database layer: the system can bootstrap a PostgreSQL-backed configuration store, resolve broker definitions and endpoints, and seed base market-access metadata for Binance. The repository also contains scaffolding for a fuller trading pipeline, but that pipeline is not yet complete end to end.

## Architecture

| Project | Responsibility | Current State |
| --- | --- | --- |
| `TradingCore` | Console entry point; wires DI, database context, and broker repository. | Implemented and buildable. |
| `DBManager` | Persistence layer using EF Core and Npgsql; owns broker/endpoint/parameter/unit models, migrations, repositories, and supporting services. | Most complete module. |
| `Brokers` | Broker-facing abstraction layer; currently wraps `BrokerService` to retrieve broker metadata by endpoint type. | Partially implemented. |
| `API` | Thin outbound HTTP client wrapper (`HttpClient`) for REST calls. This is not a web API host. | Minimal implementation. |
| `Strategy` | Intended strategy layer; contains a simple indicator-based signal rule set. | Incomplete and not currently buildable. |
| `TradeManager` | Intended trade lifecycle service boundary. | Present but effectively empty. |
| `OrderManager` | Intended order execution/routing layer. | Placeholder only. |
| `LedgerCore` | Intended ledger/accounting boundary. | Placeholder only. |
| `Utility` | Shared utilities; currently a circular linked list implementation. | Implemented, narrow scope. |
| `Agent` | Intended orchestration layer for data ingestion, indicator calculation, and signal generation. | Repository-only, not buildable in current state. |
| `UnitTests` | NUnit test project. | Present, but blocked by upstream build issues. |

### How the implemented modules interact

The actual buildable path today is:

`TradingCore` -> `DBManager.AppDbContext` / `IBrokerRepository` -> `Brokers.Broker` -> `DBManager.BrokerService` -> PostgreSQL metadata

This path retrieves broker configuration from the database and selects broker endpoints by semantic type such as `GetCandles`.

## System Flow

### Current implemented flow

1. `TradingCore` loads configuration from `appsettings.json`.
2. It registers `AppDbContext` with PostgreSQL and binds `IBrokerRepository` to `BrokerRepository`.
3. It constructs a `Brokers.Broker` instance for `BINANCE`.
4. `Broker.GetChart()` calls `BrokerService.GetBrokerWithEndpointsByTypeAsync(...)`.
5. `BrokerRepository` queries the database for the broker plus its endpoints and parameters for `EndpointType.GetCandles`.
6. The resolved broker metadata is returned and written to the console.

### Intended end-to-end trading flow in the repository

The repository structure suggests the target pipeline is:

`market data` -> `indicator calculation / analysis` -> `signal generation` -> `trade creation` -> `order execution` -> `ledger / persistence`

However, only parts of this are currently implemented:

- Market-access metadata is stored in the database.
- A REST client exists for outbound HTTP calls.
- A draft strategy rule exists using RSI, Stoch RSI, and Bollinger Bands.
- Execution, trade persistence, and ledger posting are not implemented end to end.

## Project Structure

- `TradingCore/`: executable entry point and runtime configuration.
- `DBManager/Data/`: EF Core `DbContext`.
- `DBManager/Models/`: broker, endpoint, parameter, unit, and trade domain models.
- `DBManager/Repositories/`: repository abstraction and EF-backed implementation for brokers.
- `DBManager/Services/`: service layer around brokers and unit conversion.
- `DBManager/Migrations/`: schema evolution and seed data.
- `Brokers/`: broker abstraction and metadata lookup wrapper.
- `API/`: outbound REST client and DTO placeholders.
- `Strategy/`: signal logic abstraction and indicator-based strategy draft.
- `TradeManager/`: trade-management service boundary.
- `OrderManager/`: order-execution placeholder.
- `LedgerCore/`: ledger/accounting placeholder.
- `Utility/`: shared utility classes.
- `Agent/`: orchestration prototype for live analysis and trading flow.
- `UnitTests/`: NUnit tests.

## Technologies Used

- C#
- .NET 9
- Entity Framework Core
- PostgreSQL
- Npgsql
- Microsoft.Extensions.DependencyInjection / Configuration
- NUnit

## Design Decisions

- **Modular project layout**: responsibilities are separated by concern rather than collapsed into a single application project.
- **Repository/service layering**: database access is isolated behind repository and service abstractions.
- **Metadata-driven broker integration**: broker base URLs, endpoints, and parameters are stored in the database instead of being fully hardcoded in code.
- **Strategy boundary**: `IStrategy` indicates an intended strategy-pattern approach, even though the implementation is still incomplete.
- **Shared utility isolation**: common data structures and helper logic are kept in a separate project.

## Current Status

### Implemented

- PostgreSQL-backed EF Core context with migrations.
- Seed data for Binance broker metadata and candle endpoint definition.
- Broker repository and service for querying broker definitions and endpoint metadata.
- Unit conversion model and service.
- Console startup path in `TradingCore`.
- Basic REST client wrapper.

### Incomplete or experimental

- `Strategy`, `Agent`, and `UnitTests` do not currently build because strategy code references missing types such as `CandleData` and missing namespaces.
- `TradeManager`, `OrderManager`, and `LedgerCore` are largely placeholders.
- The `Trade` model exists, but it is not registered in `AppDbContext` and is not part of the current EF migrations, so trade persistence is not implemented.
- The broker layer does not yet translate database endpoint metadata into executable broker requests end to end.
- The projects included in `TradingHub.sln` build successfully, but with warnings, including nullable-reference issues and EF Core package version conflicts.

## Future Improvements

- Complete the market-data domain model and restore buildability of `Strategy` and `Agent`.
- Add real broker request construction from stored endpoint/parameter metadata.
- Implement trade persistence and include `Trade` in the EF Core model and migrations.
- Build out `TradeManager`, `OrderManager`, and `LedgerCore` so signal generation can progress to execution and bookkeeping.
- Standardize package versions to remove EF Core dependency conflicts.
- Expand tests once the orchestration and strategy layers are stabilized.
