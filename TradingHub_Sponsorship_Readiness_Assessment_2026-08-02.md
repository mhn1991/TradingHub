# TradingHub — Sponsorship-Readiness Assessment

**Date:** 2026-08-02
**Scope:** Full-repository review (README/docs, architecture, source, tests, deployment, security, licensing, UI, git history) to evaluate readiness for commercial sponsors, open-source sponsors, research funding, strategic partners, or investors.

Evidence gathered by direct inspection: repo structure, `git log`/`shortlog`, README, 39 `.csproj` files, `TradingHub.slnx` (29 projects), the project's own prior audit reports, a live `dotnet build` (succeeded, 0 warnings/errors), a live test run, and targeted greps across all 811 `.cs` files. No repository files were modified as part of this assessment.

---

## 1. What problem it solves

An agentic, multi-broker system for **market analysis → simulated backtesting → gated live/demo execution** of discretionary-style trading strategies, with an explicit safety layer (risk sizing, daily/weekly/consecutive-loss trips, reconciliation, protective stops) sitting between signal generation and order placement. It also contains a genuine quant-research subsystem: walk-forward planning, purged time-series cross-validation with embargo (`QuantResearch/Validation/WalkForward.cs`), and five Monte Carlo stress methods (`QuantResearch/Validation/MonteCarlo.cs`).

## 2. Who would benefit

Currently: the author. Realistically, next: individual quant/systematic traders who want backtest infrastructure with real leakage controls, and small prop/research shops evaluating build-vs-buy for backtesting+risk-gating infra. It is not close to something a retail non-technical user could benefit from (no packaged install, no SaaS, no license permitting redistribution).

## 3. Differentiation from existing platforms

The genuinely distinctive pieces:
- **Purged walk-forward CV with embargo actually wired into the calibration pipeline** (`QuantResearch.Training/Pipeline/CalibrationTrainingPipeline.cs:759` calls `PurgedTimeSeriesCrossValidator.Split`), not just described in docs.
- **Hard architectural separation** between decision-making, risk policy, and execution (`Agent` never imports `Brokers`/`RiskManager`; verified independently in the repo's own audit, §2.2 of `TradingHub_Independent_Audit_Report.md`).
- **Environment-neutral design**: no `IsLive`/`IsDemo` flags in the decision path — confirmed via grep, matches the self-audit's finding.
- **Reduce-only, OCO-preserving stop amendment** and reconciliation-driven safety escalation.

This is a real, unusual level of rigor for a solo project. But none of it is unique in the industry (QuantConnect, Backtrader/Zipline+vectorbt, Freqtrade, institutional walk-forward tooling) — the differentiation is *discipline*, not a novel algorithm or asset class.

## 4. Does the code support the claims?

Mostly, with important asterisks:
- Solution **builds clean** (`dotnet build TradingHub.slnx -c Release` → 0 warnings, 0 errors, verified directly).
- A sampled test project ran clean (`TradingHub.UnitTests`: 60/60 passed in 801ms).
- The project's own July 20 audit reports **832 Simulator.Tests / 73 QuantResearchRunner.Tests**, with **2 pre-existing, known, unrelated failures** each — i.e. not a fully green suite, but close and the failures are tracked, not hidden.
- **However**, three High-severity findings from the project's own July 17 audit remain open (not marked RESOLVED in `TradingHub_Independent_Audit_Issue_Register.md`, unlike PERSIST-01/PROMO-01 which are):
  - **RSK-01**: `RiskManager/Risk/PositionSizing.cs` FixedQuantity mode approves with **zero risk/margin checks** when `LiveOpportunityCoordinator` silently defaults a missing FX rate to `0m` — both position- and portfolio-level risk gates are defeated by the same condition simultaneously. Confirmed default mode is `PositionSizingMode.FixedQuantity` (`RiskManager/Risk/PositionSizing.cs:20`).
  - **LIVE-01**: the live pipeline never evaluates trading-condition filters (session/spread/rollover) that the simulator enforces — `LiveTradingHost/Program.cs` passes `tradingConditions: null`.
  - **CAL-01**: live calibrated position management is **silently permanently inert** — a hardcoded `"Live"` volatility bucket never matches a real cohort, so calibrated management always falls back to the static default with no error signal.

So: the *research/backtest* claims are well-supported by working, tested code. The *"promoted policy transfers safely to live"* claim is **not** currently supported by the code — the project's own audit says so explicitly, and this was independently re-confirmed against the relevant code paths.

## 5. Strongest technical/commercial features

1. Purged/embargoed walk-forward CV genuinely wired into calibration, not decorative.
2. Monte Carlo stress testing with 5 distinct perturbation methods including clustered-loss stress and slippage/spread perturbation.
3. Hard architectural boundary between decision, risk, and execution, independently verified.
4. Fail-closed defaults: real-money live hard-rejected, Demo/Practice-only writes, writes default off (`LiveTradingHost/appsettings.json`: `BrokerWritesEnabled: false`).
5. Genuine self-audit culture — multiple detailed, dated, adversarial audit passes with issue registers and same-day fixes tracked with before/after test counts. This is unusually good engineering hygiene for a solo project.

## 6. Weaknesses, incomplete systems, and risks

- **Three open High-severity live-safety bugs** (§4 above), self-identified by the project itself.
- **No LICENSE file anywhere in the repo** (only third-party licenses under `node_modules`). Legally this is "all rights reserved" by default — a blocker for open-source sponsorship and ambiguous for commercial partners.
- **No CONTRIBUTING, CODE_OF_CONDUCT, SECURITY.md, or CHANGELOG.**
- **Zero screenshots, GIFs, demo videos, or sample output artifacts** in the repo — despite a Vue dashboard existing, there is nothing visual to show a prospective sponsor.
- **`Dashboard/src/components/SimulatorPanel.vue` is 3,417 lines** — a monolith, self-flagged in the project's own follow-up audit (`TradingHub_Codebase_Audit_Further_Work_2026-07-19.md §3.1`).
- **Git history is not evidence of iterative maturity**: 21 commits total, mostly generic "sync repo"/"sync local and remote repos" messages, spanning 2026-07-11 to 2026-08-01 — **three weeks old**. This reads as periodic squash-syncs of local work, not a legible commit history a reviewer could audit incrementally.
- **No Docker/docker-compose** — a new developer needs local .NET 10, PostgreSQL, and a manual encrypted-credential-vault bootstrap (`Implementation&PhasingDocs/BROKER_CREDENTIAL_VAULT.md`) to run the full stack.
- **Live host is OANDA-only** despite Binance/IG adapters existing under `Brokers/` — multi-broker live claims are aspirational.
- **No historical bid/ask data source** — fills use midpoint + configured spread/slippage (README §"Remaining production work"), a real limitation for cost-realism claims.
- Root directory is cluttered with **20+ large standalone audit/blueprint markdown files** (`TradingHub_*.md`, several hundred KB combined) rather than an organized `docs/` tree — hurts first impressions badly.
- Single developer, heavily AI-pair-programmed (commit messages and a `resume` file expose Claude/Codex session IDs) — this isn't disqualifying, but it means the project has no track record of a second engineer independently validating design decisions.

## 7. Could another developer install and run it successfully?

Plausibly, but with real friction: PostgreSQL setup, `dotnet ef database update` with a manually-supplied connection string, encrypted broker-credential vault bootstrap, then three separate processes (`LiveTradingHost`, `DashboardLive`, `Dashboard` via `npm run dev`). No containerized path, no one-command setup, no CI badge in the README to signal "this currently builds." It *does* build cleanly right now (independently verified), but a prospective sponsor evaluating from GitHub alone has no such assurance visible.

## 8. Is it trustworthy enough for an external sponsor?

Partially. Positive signals: no secrets found in the repo (scanned for hardcoded keys/passwords — none found), `.env*` correctly gitignored, broker credentials moved to encrypted PostgreSQL storage, real-money live hard-blocked, deterministic builds with `TreatWarningsAsErrors=true`. Negative signals: no license, no governance docs, three open High-severity safety bugs disclosed by the project's own audit but not yet fixed, and no independent (non-self-authored) validation exists.

## 9. Missing documentation, tests, demos, licensing, governance

- License: **missing** (see §6).
- Governance/community docs: **all missing** (CONTRIBUTING, CODE_OF_CONDUCT, SECURITY.md).
- Demos: **none** — no screenshots, no recorded run, no sample report checked into a visible location.
- Tests: present and substantial (~900+ tests across `Simulator.Tests`, `LiveTrading.Tests`, `TradingCore.Tests`, `TradingHub.UnitTests`, `QuantResearchRunner.Tests`, `DBManager.Tests`, plus gated `Brokers.IntegrationTests`), but with a handful of known persistent failures and no visible CI badge/history proving they pass over time (the CI workflow at `.github/workflows/ci.yml` runs build+test on push/PR, but there's no way to see historical run results without visiting GitHub Actions directly).
- Docs: abundant in *volume* but poorly *packaged* — genuinely useful architecture docs exist (`Implementation&PhasingDocs/ARCHITECTURE_SIMULATOR.md`, `QUANTITATIVE_RISK_AND_MTF.md`, etc.) but are buried among audit/blueprint files with no index or table of contents pointing a newcomer to the right one.

## 10. IP, security, and regulatory concerns

- **IP**: no license means the author retains all rights by default — fine for the author, but blocks anyone from legally reusing/forking the code, which limits open-source sponsorship models specifically.
- **Security**: credential handling is sound (encrypted vault, no secrets in repo). No SECURITY.md means no disclosed vulnerability-reporting process, which sponsors/partners will expect.
- **Regulatory**: this is trading/execution software touching real (if currently gated) broker accounts. There is no visible compliance disclosure, no disclaimer about financial risk, no statement about jurisdictional restrictions, and no evidence of legal review. Any commercial or investor conversation will need this addressed — algorithmic trading tools attract regulatory scrutiny (e.g., broker API ToS compliance, potential registration questions if ever offered as a service to third parties) that isn't touched anywhere in the repo.

## 11–12. Who might sponsor, and what value they'd get

| Sponsor type | Realistic interest | Value received |
|---|---|---|
| Quant research groups / academic labs | The purged-CV/walk-forward/Monte Carlo tooling as a reusable research artifact | A tested, leakage-aware backtesting harness they don't have to build |
| Fintech infra vendors / brokers | The safety-gating architecture (risk gates, reconciliation, environment-neutral pipeline) as a reference implementation or acquihire-style interest | Proof-of-concept for "safe agentic execution" they could productize |
| Individual OSS/GitHub sponsors | Unlikely at this stage — no public track record, no license, no community | N/A until licensed and public-facing |
| VC/angel investment | Unlikely — no team, no live track record, no revenue model, explicit self-admission of "not production-ready" | N/A |
| Grants (research funding bodies) | Possible if reframed as an open research tool with reproducibility as the pitch | Citable, reusable methodology for leakage-safe backtesting |

## 13. Most realistic funding route

**Not** general open-source sponsorship or investment right now. The most realistic paths, in order:
1. **Targeted research grant or academic/industry collaboration** around the walk-forward/Monte Carlo/leakage-control tooling specifically — that's the most defensible, differentiated, working asset.
2. **Consulting/contract work** — the demonstrated skill (safety-gated agentic execution architecture) is sellable as expertise even before the product is.
3. **Open-core commercialization** — plausible eventually (license the core, sell hosted execution/monitoring), but requires the licensing, governance, and live-path fixes in §14 first.

## 14. What should be completed before contacting sponsors

1. Fix RSK-01, LIVE-01, CAL-01 (all self-identified, High severity, live-path).
2. Add a LICENSE (and decide the model — this gates every other funding route).
3. Add CONTRIBUTING.md, SECURITY.md, a short CODE_OF_CONDUCT.
4. Produce at least one real screenshot/GIF of the dashboard and simulator in action, and one sample walk-forward/Monte Carlo report artifact checked into `docs/` or linked from README.
5. Consolidate the 20+ root-level audit/blueprint markdown files into an organized `docs/` tree with an index; keep 2-3 headline documents visible at root.
6. Add a CI status badge to the README and confirm the workflow is green.
7. Provide one no-friction path to run the stack (Docker Compose at minimum).
8. Write an explicit financial-risk disclaimer and a short regulatory-posture statement.
9. Split the 3,417-line `SimulatorPanel.vue` — a sponsor's engineer looking at that file will read it as a maintainability red flag.

## 15. Roadmap

**30 days**: fix RSK-01/LIVE-01/CAL-01; add LICENSE + SECURITY.md + CONTRIBUTING.md; capture dashboard screenshots/GIF; add CI badge; write risk disclaimer.

**90 days**: consolidate docs into an indexed `docs/` tree; add Docker Compose; split `SimulatorPanel.vue`; publish one real, reproducible walk-forward + Monte Carlo report as a checked-in artifact with methodology notes; establish a real (non-squashed) commit history going forward.

**Six months**: run a supervised, small-scale live Practice/Demo track record over that period as evidence; decide and execute the funding-route decision from §13 (grant/consulting vs. open-core); if pursuing open-core, define what stays closed (live execution/hosting) vs. open (research/backtesting core).

---

## Scores (out of 100)

| Dimension | Score | Basis |
|---|---|---|
| Technical maturity | **55** | Sophisticated, tested architecture with real leakage controls, but 3 open self-identified High-severity live-path bugs and only 3 weeks of visible history |
| Documentation | **40** | High volume, low usability — no index, no visuals, cluttered root, real docs exist but are hard to find |
| Product usability | **30** | No containerized setup, multi-step manual bootstrap, no demo material, 3,400-line UI monolith |
| Commercial potential | **50** | Genuinely differentiated research tooling; no license, no track record, no packaging limit near-term commercial motion |
| Sponsor confidence | **40** | Good secrets hygiene and safety defaults undercut by missing license/governance and known unresolved safety bugs disclosed by the project's own audits |
| **Overall sponsorship-readiness** | **43 / 100** | |

**Five strongest selling points**: (1) purged walk-forward CV wired into a real calibration pipeline; (2) multi-method Monte Carlo stress testing; (3) architecturally enforced separation of decision/risk/execution, independently verifiable; (4) fail-closed live-trading defaults (real-money hard-blocked, Demo-only writes, writes off by default); (5) an unusually rigorous, adversarial self-audit culture with tracked, dated, verified fixes.

**Five largest obstacles**: (1) no LICENSE; (2) three open, self-identified High-severity live-safety bugs; (3) zero visual/demo evidence anywhere in the repo; (4) no packaged install path (no Docker, manual DB+vault bootstrap); (5) three weeks of squashed "sync" commit history with no legible development track record.

**Recommended funding model**: targeted research grant / academic-industry collaboration around the backtesting-and-validation tooling, plus paid consulting on the safety-gating architecture — not general OSS sponsorship or investment at this stage.

**Sponsor-facing description** (only what the evidence supports): *"TradingHub is a .NET 10 research platform for building and safety-gating agentic trading strategies, combining a leakage-aware backtesting/walk-forward pipeline with an architecturally separated risk and execution layer. It is under active, single-developer construction, builds cleanly, and carries a substantial (900+) automated test suite, but is explicitly not yet production-ready: live-path parity with its validated simulator policies has open, disclosed gaps."*

**Recommendation: prepare further before approaching sponsors.** The research/backtesting core is real and differentiated enough to eventually justify a grant or consulting conversation, but going out now — with no license, three self-identified open safety bugs, and zero demo material — would undersell the legitimate technical work and risks a credibility hit with any sponsor who reads the repo's own audit reports (which are candid and would be the first thing a diligent evaluator finds).
