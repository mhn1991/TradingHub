# PostgreSQL retention

TradingHub retains normalized decisions, candidates, reservations, orders, fills, positions,
management actions, safety and reconciliation records, runtime sessions, deployments, policy
revisions, configuration hashes, and calibration lineage indefinitely. The retention command
only targets observation data that can be reconstructed from those durable records.

Run a preview first:

```bash
dotnet run --project DBManager.Cli -- retention --dry-run
```

Apply the same policy explicitly:

```bash
dotnet run --project DBManager.Cli -- retention --apply
```

Defaults retain uncorrelated, information-level agent state telemetry for 90 days and aggregated
agent activity windows for 365 days. Detailed rows are eligible only when they have no simulation,
research, deployment, policy, decision, candidate, reservation, order, fill, or position identity.
Lifecycle, incident, data-quality, market-health, persistence, safety, and reconciliation events are
never removed by this job. The command emits counts, cutoffs, deletion counts, and elapsed time as
JSON. `--detail-days` and `--aggregate-days` can lengthen the windows; detail cannot be below 30 days
and aggregate retention cannot be shorter than detail retention.

Schedule `--dry-run` daily for monitoring and `--apply` only after alerting/metrics collection is in
place. Table partitioning remains a volume-triggered operation: add monthly partitions when query
and vacuum measurements show that the current indexed tables no longer meet the reporting SLO.
