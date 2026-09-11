# Archived Alfonso research scripts

Ad-hoc scripts written during the Alfonso investigation (PROJECT_STATE §3.26–§3.52). They lived in a
scratch directory outside version control; they are preserved here because §3.47 is a standing lesson
about exactly that — 3.45/3.46 could not be checked, and had to be retracted, **because the script
that produced them was not saved**.

**These are archived, not maintained.** They are not part of any build or test, nothing depends on
them, and several will not run without data that no longer exists. Read them as a record of how a
number was produced, not as tools.

For the maintained equivalents, use these instead:

| want | use |
|---|---|
| regenerate the study bar CSVs | `tools/alfonso_export_bars.py` |
| §3.51/§3.52 zone-level tables | `tools/alfonso_grade_report.py` |
| §3.50/§3.52 strategy A/B tables | `tools/alfonso_ab_report.py` |
| §3.47's lookahead/concurrency/gap report | `tools/alfonso_lookahead_report.py` |

## What is here

| script | what it does | state |
|---|---|---|
| `diag.py` | §3.47's lookahead A/B with timeout accounting; a variant of the section-A table | **runs** — reproduces 4,995 entries at `doff=-1` |
| `run_portfolio.py` | portfolio simulation driver over the §3.47 rule | **runs** |
| `monot.py` | a four-line stub — imports only, no body. The monotone-leakage table in §3.47 was not produced by this file | **dead** |
| `analyse_cand.py` | aggregates `cand-{instrument}.csv` candidate logs | needs candidate logs from a run |
| `reparse.py` | tolerant candidate-log CSV reader, written when unquoted `accomplished` flags shifted every later column | needs candidate logs |
| `screen.py` | instrument screening over `long-*-{h4,d1}.csv` | needs those CSVs |
| `make_csv.py`, `make_csv_b.py`, `make_csv_f.py` | parse trade reasons out of dashboard strategy dirs into CSV; three near-identical variants for three run families | reference run directories that may be gone |
| `summarise_pm.py`, `summarise_pm2.py` | summarise position-management sweeps | see the `rMultiple` warning below |
| `summarise_stopatr.py` | summarise the stop-ATR sweep | references `c-*`/`c2-gold` run dirs |
| `extract_full.py` | extracts bars from a `v2-p0b` experiment's market chunks | points at a different experiment's scratch output; **almost certainly dead** |

## Two traps if you reuse any of this

**`summarise_pm.py` reads `rMultiple`.** §3.44 established that `rMultiple` in the result JSON divides
by risk *plus* an assumed round-trip cost, so it is not profit-per-risk and understates tight-stopped
winners badly. Anything derived from it needs recomputing as
`netProfitLoss / (|entry - stop| * quantity)`. The maintained tools already do this.

**Paths were hardcoded to `/mnt/storage/scratch/alfonso`.** The three scripts that import the
portfolio library (`diag.py`, `run_portfolio.py`, `monot.py`) were rewired to import
`tools/alfonso_portfolio.py`; the rest still carry absolute scratch paths and will need editing.
