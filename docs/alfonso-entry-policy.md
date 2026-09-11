# Explicit entry policies

`--alfonso-entry-policy core` is the unchanged default. With the default sequence,
4h owns direction, 1h provides context, and entries normally use 15m zones.
The existing core matrix also allows a nested 1h-zone entry in its designated
out-of-alignment scenario; this change does not silently remove that route.

`--alfonso-entry-policy lower-reversal --alfonso-confirm-entry` selects a separate
experimental, reversal-only arm. It accepts only these alignments:

| 4h | 1h | 15m | Permitted entry |
| --- | --- | --- | --- |
| Down | Up | Up | Buy at a 15m demand zone after confirmation |
| Up | Down | Down | Sell at a 15m supply zone after confirmation |

These labels describe the default sequence; custom intervals keep their assigned
top/middle/lower roles. Unknown or out-of-alignment top trends are still refused.
This policy does not guarantee entry at the first visible reversal, and it does
not add core entries. Decision/candidate reasons identify the experimental arm.

Confirmation uses the existing zone-touch and closed-candle rejection path:
price must have tested the zone and then closed back beyond its proximal edge
without invalidating it. With the default fresh-level rule, only the first
pullback qualifies. Entries use the confirmation close, not a hindsight fill at
the zone. Missing `--alfonso-confirm-entry` is a configuration error.

Range, control, zone validity, nesting options, cost and risk gates still apply;
this policy does not override them. Stop placement is unchanged (entry-zone
distal plus configured padding); risk/target use the actual entry reference.
Compare the reversal arm against core with `--alfonso-confirm-entry` as well to
separate the policy effect from changing entry timing. Other trend experiments
can be enabled independently, but can also neutralize the top and block entries.

This is a testable strategy extension, not a proven performance fix. The saved
historical dashboard runs are not overwritten or relabeled by these options.

Verification: 267 Alfonso regression tests passed, including all 64 alignments
for each policy and configuration/confirmation validation. BacktestRunner built
without warnings or errors. These checks do not measure multi-market P&L.
