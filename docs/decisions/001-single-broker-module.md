# ADR 001: Single broker module with configured instances

## Status

Accepted.

## Context

The initial rewrite placed OANDA, Binance, and broker abstractions in separate projects. The preserved `baseline-v1` implementation showed a different intended direction: one broker module, runtime broker metadata, factory/DI construction, and conversion from provider responses to one internal candle type.

The baseline idea was sound, but its implementation was unfinished. In particular, instruments inherited from broker classes, interfaces were empty, HTTP results used raw `List<object>` values, and credentials/endpoints were moving toward database records without a typed protocol boundary.

## Decision

- Keep all external broker providers in `TradingHub.Brokers`.
- Represent each runtime account with a `BrokerDefinition` loaded from configuration.
- Use `IBrokerFactory` to select an `IBrokerProvider` and construct an `IBrokerGateway`.
- Allow multiple configured instances of the same provider.
- Resolve secrets through `IBrokerCredentialStore`, not broker JSON or database endpoint rows.
- Keep provider DTOs and authentication/signing logic internal to provider folders.
- Map all provider responses into canonical TradingHub contracts before leaving the module.
- Share endpoint validation, ambiguous HTTP classification, live-account guards, and result creation.

## Consequences

Adding another OANDA or Binance account is configuration-only. Adding a new provider requires provider code and one DI registration, but no new project. Provider protocol differences remain explicit and testable instead of becoming untyped endpoint metadata.
