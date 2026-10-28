# Stage 4: price snapshot validation and recovery

## Delivery boundary

This records the independent data/valuation milestone of **CAT-002**. At that
milestone no source or public TT basis was active. The user subsequently selected
the [concrete policy and basis](PRICE_AND_TT_PROPOSAL_2026-10-28.md); product integration
is recorded in the [public reference contract](STAGE4_PUBLIC_REFERENCE.md). The
legacy pinned `ApiPriceWorkload` remains a reproducible research comparator and an
explicitly enabled labelled fallback, independently of the public TT ruler.

## Closed, immutable snapshot

`api-price-catalog/v1` contains origin, source name/URIs, nullable source retrieval
time, USD/per-million-token units, explicit reference context and sparse price rows.
Each row names exact model, product, tier, context band, disjoint category and
applicable effective interval. Cache-write rows additionally require a lifetime;
non-write rows cannot accidentally depend on that field. Null prices are unknown;
explicit zero is a known zero price.

Validation rejects unsupported currency/units, negative values, duplicate/overlapping
intervals, unsupported schema and malformed coordinates. Official-curated origin
requires clean HTTPS OpenAI-domain source references; it labels local curation, not
provider attestation. Overrides and community/legacy fallbacks keep separate origins.
No alias/model-capacity inference is used to choose a price. Canonical content IDs
cover source/units/provenance and normalized coordinate/category/date/price values.
Caller arrays are copied and exposed read-only. Maximum normalized rates: 4,096;
maximum file/archived payload: 512 KiB.

The local reader accepts the normalized schema or the explicitly versioned curated
candidate format already supplied with the proposal. It keeps closed numeric/status/
provenance fields and an input-artifact digest, with no raw JSON, unknown instructions
or credential fields. URI user-info, query strings and fragments are rejected. No
feed content is executed and no network request is made. Errors use fixed diagnostics.

Nullable effective dates remain unknown. Timestamp fields require an explicit time
and offset: a date-only or zone-free string cannot become an invented UTC instant.
Unknown retrieval time is also supported, including legacy data; import/file time
does not replace the original verification clock.

## Valuation and source precedence

`api-price-valuation/v1` explicitly receives a reference context, valuation mode,
calculation time and maximum source age. It does not infer actual execution tier,
context length, cache lifetime, region, modality or tools from native counts.

| Mode | Date treatment |
|---|---|
| SnapshotReference | Revalues the same quantities under the supplied snapshot/context at the requested reference time. Unknown effective starts can remain reference assumptions; known expired/future intervals do not apply. This is the current-price-reference route. |
| DateEffective | Requires a supplied verified event time and a known effective start, with half-open `[from, until)` coverage. Unknown starts and unverified event times stay unpriced. Collection/retrieval time is not substituted. |

For each positive disjoint quantity, a numeric explicit override takes precedence,
then the supplied primary snapshot, then an explicitly enabled labelled fallback.
A null override contributes no replacement; a zero override does. Source contributions
retain their snapshot ID, origin, name, amount and priced quantity. Community/legacy
primary use also requires explicit fallback permission. No source preference or
fallback is selected globally by this library.

Partial results keep supported subtotals and priced/unpriced quantities; whole amount
stays null when any positive quantity is unpriced. Inconsistent quantities/diagnostics
and arithmetic limits do not become plausible zero quotes. Zero amounts do not
require irrelevant missing metadata, such as cache lifetime for non-write work.
Stale, future-clock and unknown-clock flags describe only sources actually used.
The caller supplies the age threshold; the proposed 30-day policy is not a default.

The exact finite-decimal arithmetic from joint TT scoring is now shared internally.
TT's calculation/version and existing scalar artifacts are unchanged. API reference
dollars have no TT normalization and do not represent native billed dollars, credits,
subscription quota or a budget balance.

## Retention and failure recovery

Intelligence schema 10 adds price snapshots and per-source import-attempt state under
the existing repository. Snapshot retry is idempotent for unchanged bytes; conflicting
content for an identity is rejected without replacement. Original import time and
source retrieval/effective times survive retry and restart. Failed imports retain
the last good ID and their own failure state. Prior versions remain available by
explicit ID or bounded history reads (at most 20). Invalid stored snapshots are
unavailable and are not silently overwritten; a retained older version can still be
selected explicitly. No age deletion is introduced.

Reads are read-only and do not initialize missing tables. Schema migration is atomic;
an obstructing view fails without advancing the marker. Neither importing nor reading
a source changes the active price card, TT basis, original TT scores or model catalog.

## Explicit developer commands

- `--price-preview <app-db> <file> <from-utc> <to-utc> <max-source-age-days>` validates a
  supplied file and calculates a read-only snapshot-reference preview over selected
  native increments. It requires an explicit reference context in the file and a
  past range of at most 366 days. It never imports or activates anything.
- `--price-import <app-db> <source-key> <file>` records a validated snapshot or failed
  attempt. It requires an existing initialized TajsTokens database, verified before
  any write; arbitrary/native SQLite files are rejected. An invalid import returns
  exit 2 and preserves last good data. This is an explicit archive operation.
- `--price-source <app-db> <source-key>` inspects retained last-good data and latest
  attempt state without source access or activation.

## Validation and installed evidence

The full Core suite passed **831 tests** (1m51s); Windows build passed with zero
warnings/errors (53.23s), installing/restarting the daily app at 2026-10-28
11:49:28 UTC. Tests cover canonical identity, units/currency/interval validation,
coordinate separation, dated versus undated lookup, zero/null overrides, opt-in
fallback labels, missing model/category/lifetime, source clocks, arithmetic limits,
original times, restart/retry/version retention, malformed-checksum neighbors,
privacy allowlisting, date-only rejection and migration rollback. Existing TT and
forecast/accounting tests also passed.

Installed preview of the same September 27–October 27 selection (20,078 events /
3,984,535,029 native tokens) returned **1,216.5290533 reference USD**, with no unpriced
quantity, under the proposed five-model snapshot. The normalized snapshot ID was
`api-prices:1e01fb5a95cb2899cbfd112bfd56171939e4d66fbcad2ccbc347b0665d7296af`.
The unchanged frozen candidate still returned **608.26452665 TT**, exactly half that
same-schedule reference amount. These are deterministic reference calculations,
with no claim to actual bills, account completeness or quota prediction quality.

Installed import/recovery checks wrote only to an isolated app-owned fixture: a good
snapshot was retained; a bad contract returned exit 2; restart/read kept the original
snapshot and its 11:09:02.566 UTC source time. A separate non-app SQLite fixture was
rejected with exit 2 and byte-for-byte unchanged. No actual native database was used
for that write guard test.

Backup/live comparison preserved all 96,469 prior native rows, 513 counter-input
journal rows and six original research snapshots; three normal native/journal rows
arrived afterward. Telemetry/observatory/state-index versions stayed 16/11/8, with
intelligence 9 → 10. Daily price snapshot/import-state and joint-TT tables remained
empty. Fresh pace receipts still reconstructed the existing reported calculations.

Installed SHA-256 values:

- Core: `53709A89307F6809183A12AC6C73AC8A20674FD81FCE9E72279F55EF53A517EC`
- Infrastructure: `5EC3FA672A4BEFBE1E6AFF98B567EA8A27E5FCB5788D4E64E4255992F589AB48`
- Evaluation: `CE8CF9A063995346A8018202ED27D583400792899B2A1DABC499039D12374C3F`
