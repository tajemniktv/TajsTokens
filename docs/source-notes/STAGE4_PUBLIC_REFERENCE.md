# Selected TT basis and independent API reference prices

The user selected DEC-001/T-01, DEC-002 and DEC-005 on October 28, 2026. The
[reviewed definition](PRICE_AND_TT_PROPOSAL_2026-10-28.md) specifies the exact weights,
basket, artifact digest, standard/short/30m reference semantics and extension rules.
One TT is one million uncached GPT-6.1 Sol input tokens. There is no extra effort
multiplier, inferred execution tier, quota conversion or paid reference task.

## Selection and recovery

`workload-reference-selection/v1` appends explicit selection events in intelligence
schema 11. A selection contains a full immutable TT definition and independent
primary/override/fallback price source keys. Actual selection time is retained.
The most recently appended event selects the ruler, including after a clock rollback;
concurrent changes must match the prior selection identity instead of silently
reverting a newer TT choice. Payload bounds, checksums and canonical basis/selection
identities are verified on read. A damaged current selection stays unavailable;
startup does not replace it with defaults or silently select an older ruler.

First product use initializes the approved embedded artifacts only if absent.
Original price verification time is retained; import/startup time never refreshes
that clock. Exact JSON bytes are preserved by `.gitattributes`, so clean checkouts
embed the same reviewed artifact digest, including line endings. Restart and
concurrent initialization preserve prior selections and newer imported prices.

Official imports update the selected source's versioned last-good snapshot. Failed
or unavailable imports retain it and expose the latest import state. Prices become
stale after 30 days from their original verification clock. Overrides, community
fallbacks and the legacy pinned card keep distinct roles and contribution labels;
none is silently enabled. The legacy card's verification clock remains unknown.
There is no network polling. All native data and credentials remain read-only.

## Shared product calculation

The existing intelligence engine scores the selected native joint workload and
calculates API-reference amounts in one product snapshot. TT uses the selected
frozen definition. Current-price reference uses the selected official snapshot's
explicit context with selected overrides/fallbacks. Date-effective valuation uses
the reader's qualified source event times and known effective starts; no dates or
actual billing conditions are invented. An entirely undated schedule uses the
bounded joint cells to report unpriced date-effective quantity without allocating
another event-sized workload array.

Native tokens remain visible for missing/partial/inconsistent reference results.
The product manifest carries selection, basis and price identities; the legacy
pinned price comparator is separately named. No forecast/model-selection policy
is changed. Existing empirical category TT and `tt-lab/v4-blocks` remain separate
research artifacts, with no automatic promotion into public TT.

Overview and Planning show selected observed tokens and measured TT with its
vintage/support label. Work history carries current API-reference value, coverage
and source-age/import status. Evidence exposes exact identities, dates, modes,
source contributions and versions. File imports and explicit basis selection live
in that existing view; input changes clear old displayed TT while new work loads.
New import controls participate in lightweight hidden-dashboard state retention.

"Save this TT calculation" retains an original score with the full frozen basis,
joint workload, capture time and selected filters. Saving is explicit; opening or
refreshing history is not an automatic score-archive write. New price imports and
explicit future basis selections leave saved originals unchanged. Compatible
extensions must preserve the currently selected parent's weights and basket.
Restatement requires the existing typed archive operation and a matching original
workload, scope and capture; no automatic restatement is introduced.

## Verification

The focused suite covers approved basket normalization, independent current prices,
unknown effective dates, partial quantities, 30-day staleness, concurrent first use,
restart, last-good recovery, role validation, zero overrides, explicit legacy fallback,
selection concurrency/clock rollback, immutable originals and migration rollback.
The intelligence-engine fixture exercises the real repository and shared product
projection while preserving native quantities. The full suite passed **837 tests**
(1m54s); the Windows build passed with zero warnings/errors (57.07s) and installed/
restarted the daily app on October 28 at 16:03:33 UTC. No forecast algorithm or
chronological model-selection policy was changed.

The installed fixed September 27–October 27 range reproduced **608.26452665 TT** and
**1,216.5290533 reference USD** over 20,078 selected rows / 3,984,535,029 native tokens.
The selected basis ID is
`tt-joint:00dac6d4779ceff3507bce2570ac4a7fe30876e53e0fc033d1ae41540a46f6fa`;
price snapshot ID is
`api-prices:1e01fb5a95cb2899cbfd112bfd56171939e4d66fbcad2ccbc347b0665d7296af`.
All date-effective quantity remained explicitly unpriced, because effective starts
are unknown. Re-importing the reviewed price file preserved the selected basis,
selection identity, snapshot identity, first import time and source verification time.

Installed isolated-fixture invalid-import/reopen checks returned exit 2 and retained
last good data and the same selection/basis. The destination guard rejected a
non-app SQLite fixture with exit 2 and byte-for-byte unchanged contents. No actual
native database was used as a write-guard target.

Actual Overview showed native tokens beside **367.86 TT**, its vintage and zero
unmapped quantity for the selected week. Planning showed the same measured work
independently of forecast availability; Evidence exposed the exact identities,
source dates, disabled optional roles and separate price modes. The app's save
button retained original `b2f9b52b83d24a63b9ea887e23b53b45`, **367.8566433 TT**, with
its selected filters and capture. Independent decimal arithmetic reproduced it.
Its payload hash stayed
`cb2866aec8b40a38c081e1fabebbf672226ea78eb192681b5059c865055c1c13`
through restart/re-import. The daily store contains one price snapshot, one selection
and one explicitly saved original; optional overrides/fallbacks remain disabled.

The native import picker opened successfully, but its separate `PickerHost`
surface was absent from the Computer Use target inventory and input was refused.
No picker selection was automated or claimed. The app exited through its existing
graceful-stop event and restarted in background mode at 16:11:04 UTC, retaining the
selection and original. Actual file choice through that picker and user visual
acceptance remain open under SHIP-004. CLI import and its shared import service
were verified separately.

Backup/live comparison preserved all **96,792 prior native rows**, **836 original
counter-input rows** and **six research snapshots**, with zero changed/removed prior
rows. Seventy-four normal native/counter-input rows had arrived by the final check.
Intelligence advanced 10 → 11; the native telemetry contract was unchanged.

Installed SHA-256 values:

- App: `D2847523B590A664ADD54AFCF4EF76DCD5B6639595599147AC649365FF8397C5`
- Core: `F554D7DFA5966617F399ED74B73D572B59664B0AB773FE3504C3A7174FDE512F`
- Infrastructure: `28B75B1BC59B1C4AD54421F9D170A047EEDBDD729677A4AD1F075443D758A7F4`
- Evaluation: `4FFFF679DC46187B33AC94A8F80955B384C17804E40D75FCBB3A47E4DFEA2AE7`
