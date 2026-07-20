# Broker credential vault

Broker credentials are stored as AES-256-GCM ciphertext in
`security.broker_credentials`. DashboardLive, LiveTradingHost, and BacktestRunner load the
database first and do not require broker-secret environment variables.

The database connection and encryption key are machine-local, ignored files:

- `.state/tradinghub.database.json`
- `.state/keys/broker-credentials.key`

Keep both files mode `0600`, back up the key separately from the database, and never commit either
file. Losing the key makes the ciphertext unrecoverable.

To bootstrap or rotate credentials, copy `examples/tradinghub.database.example.json` to `.state`,
prepare a temporary ignored env file, and run:

```bash
dotnet run --project DBManager.Cli -- bootstrap --env-file /path/to/temporary.env
```

The command applies pending migrations, encrypts OANDA/Binance/IG fields, upserts one row per
broker/environment, and verifies each row by decrypting it. Commented credentials import as
disabled so they are preserved without being activated. Remove the temporary plaintext file after
successful verification.
