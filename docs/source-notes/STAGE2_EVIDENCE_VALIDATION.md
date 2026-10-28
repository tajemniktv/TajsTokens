# Stage 2 evidence and schema validation

28 October 2026, source at `2746843` before the contract documentation change.
[PROJECT.md](../../PROJECT.md#source-of-truth) owns the product contract and
[Current work](../../PROJECT.md#current-work) owns acceptance. This note records
installed evidence, not another specification or backlog.

## Installed chronology and schema boundary: A-01

The daily app remained PID 44984 throughout this check; it was not rebuilt,
restarted or given test data. Installed Core SHA-256:
`EE61E4936862FFF7AAE6F81B929B47413304F094C5386BA92B30C01C1AFFEF5D`.
Build-manifest SHA-256:
`A9E7E6F0331D73D71878861453B52A831AD845FA467FB9620E25C011F7C919CF`.
This matches the preceding validated 707-test Windows build. The app-owned database
was opened read-only, with each observation on one consistent read snapshot.

| Live observation over about 65 seconds | Before | After |
|---|---:|---:|
| Retained rollout records | 611,892 | 611,908 |
| Native token events | 95,892 | 95,894 |
| Observatory component | 9 | 9 |

Two existing source-qualified checkpoints advanced. No existing native event's
numeric amounts, original observation time or first collection time changed; none
was removed during this interval. Afterward, 90,042 events had valid source time
and 5,852 remained `LegacyUnverified`. No event marked valid lacked its source time.
The latest native collection was `2026-10-28T05:07:16.3476030+00:00`.
The remaining unverified rows are retained evidence with unknown source chronology;
their causes and recovery coverage were not classified here. These are token-event
rows, not the separately reported 67 unavailable workload origins.

The installed `TajsTokens.ForecastEvaluation.exe --calibration-worker` was also run
against a small isolated database under `.codex/temp/stage2/`:

1. Empty-database initialization succeeded with observatory component 9.
2. The fixture received one dated native increment of 123 tokens and one untimed
   increment of 57 tokens, both with unknown model/role. All fields were synthetic.
3. Changing only that fixture's component marker to 10 made the installed worker
   refuse it (exit 1, unavailable result). Marker 10 and both complete rows remained
   unchanged. This tests a newer-version refusal, not a future schema-10 migration.
4. Restoring the fixture's supported marker 9 allowed recovery (exit 0). The workload
   dataset contained exactly the dated event; all 180 stored tokens, the untimed
   event and original timestamps remained unchanged. The untimed amount was not
   restamped into forecasting history.

The fixture was seeded directly, so this is installed reader/schema-preservation
evidence, not a new parser or migration test. Existing v8-to-v9 migration, original
time/amount preservation and dated-query regressions remain automated evidence in
[timestamp tests](../../tests/TajsTokens.Core.Tests/CodexTimestampProvenanceTests.cs)
and [schema compatibility tests](../../tests/TajsTokens.Core.Tests/ObservatorySchemaCompatibilityTests.cs),
included in the preceding full 707-test pass. The installed probe does not replay
the user's pre-upgrade database or establish complete source recovery.

Only the isolated fixture's schema marker/data were changed. No native Codex store,
credentials or live TajsTokens evidence was written by the probe; no provider call
or additional source acquisition was requested. The main app continued its normal
collection. Reproduction and aggregate local outputs are
`.codex/temp/stage2/verify_installed_chronology.py`, `installed-chronology.log` and
`installed-chronology-result.json`. The result contains aggregate counts/hashes,
not source paths, account keys or event identities.

## Original counter-input journal (component 10)

Component 10 adds `codex_token_counter_observations` under the existing observatory
and token transaction owners. It retains closed DTOs containing both nullable six-
counter snapshots, source time/quality and allowed interpretation metadata. It
retains partial inputs that cannot produce a reducer delta, without manufacturing
zeros. The safe source label and known generation association are separate columns;
unavailable generation association remains null. Source text/JSON and arbitrary
extension fields are not stored.

Repeated identical input/interpretation is idempotent. Changed numeric or metadata
interpretations remain fingerprinted variants of the same source occurrence; the
existing selected delta is not overwritten or summed with those alternatives.
The journal survives generation retirement. Existing active queries continue to
use selected projections, so retained variants do not inflate current totals.

Migration starts the journal empty: old deltas are insufficient to reconstruct the
original total/last inputs. A subsequent replay preserves known occurrence times
but records the actual later input recovery time separately. There is no forced
full-history replay in this change. Superseded legacy deltas and other numeric
projection types still require the remaining EVID-004 retention work.

Seven focused journal cases cover partial/unknown inputs, two-snapshot retention,
idempotent capture, numeric conflicts, v9-to-v10 migration/recovery, transaction
failure rollback, and genuine rewrite through each of the individual/batch writer
paths. Two compatibility cases check older migration/planner paths and refusal of
component 11. This adds no forecast policy or decoder behavior.

The complete Core suite passed **714 tests** (1m41s), and the Windows solution
build passed with zero warnings/errors (1m13.30s). Normal deployment installed and
restarted the daily application, reporting PID 28688. Installed infrastructure
SHA-256: `480D26CF300E7FC9DCE0D1299988AF7D60EBE76FAF4B8092B399E3B980C5BFE4`.

Read-only installed sampling at `2026-10-28T05:28:23Z` found component 10 and three
new journal entries, all associated with source generations. Their input captures
were around `05:28:19Z`; there was no historical journal backfill. A separate
read-only comparison with the normal pre-deployment component-9 backup verified
all **95,956** pre-existing native token rows: zero removals and zero changes to
their six amounts, original observation/capture times, source event times or source
timestamp qualities. Three additional native rows were collected normally.

These checks read app-owned databases with SQLite read-only/query-only connections;
they did not modify live data or native Codex sources. Reproduction helpers and
aggregate JSON are `.codex/temp/stage2/verify_counter_journal_install.py` and
`compare_counter_journal_upgrade.py`. This proves installed migration/preservation
and ordinary new journal writes, not a user visual pass, compressed recovery or
complete superseded projection retention.

## Superseded numeric retention (component 11)

`SqliteCodexSourceGeneration` now preserves available numeric evidence before
removing an old active generation. The existing projection transaction owns both
preservation and removal: any failed archive or delete rolls back both. The token
reducer still has its existing subsequent transaction; this change does not claim
atomic publication of an entire parsed file or validate a compressed candidate.

The new retirement header records a safe file label, replacement source identity,
actual retirement time, contract version and `superseded-source-identity` reason.
Old generation membership retains session, physical size and last-seen values.
These are known provenance fields; a physical identity change alone is not claimed
to prove a semantic rewrite. Equivalent representation adoption remains ING-002.

Closed SQL `json_object` allowlists preserve native disjoint increments and original
times/quality, latest counter state with its cumulative/synthetic flag, quota
observations, context observations, workload metadata and numeric timeline fields.
Source JSON, arbitrary columns and free-form timeline summaries are excluded.
Missing values remain JSON null and zero remains zero. Old normalized increments
and counter state are labelled as such, not reconstructed original total/last
inputs. Missing legacy source-generation association remains null. Existing raw
counter and response journals survive independently.

The archive is not added to normal accounting/forecast queries. Replacement removes
the superseded active projections as before, and unrelated sources remain active.
Repeated delivery of the same selected generation creates no new retirement;
subsequent actual selections append independent retirement events without erasing
earlier snapshots. Migration creates empty archive tables, without inventing past
retirements or replacing existing observations.

Seven focused cases exercise all six numeric kinds, exact stored floating-point
values, original chronology and nullable
values, contradictory quota snapshots, unknown legacy association, exclusion of
free-form summary text, unrelated-source preservation, retry idempotence, later
selections, migration, and rollback on either archive or deletion failure. The
existing individual/batch ingestion rewrite cases additionally prove archived
native totals equal the prior selected generation while current totals count once.
The pre-existing legacy-delta retention gap is closed for future retirements;
retirements that already happened cannot be reconstructed by this migration.

The full **720-test** Core suite passed (1m41s); a subsequently added exact-
floating-point regression passed separately. The Windows build passed with zero
warnings/errors (56.81s), then normal deployment installed/restarted PID 17144.
Installed infrastructure SHA-256:
`EB35E2DC80AAAC4FF64009707DB9D23DE53B771EAD8248A190171F3E5F84BC85`.

A read-only comparison against the normal component-10 pre-deployment backup
verified installed component 11, **95,995** pre-existing native rows and **39**
prior counter-input journal rows. There were zero removals or changes to the
existing native amounts/chronology or complete journal rows. Two new native rows
and two new journal rows were collected normally. The new retirement table was
empty, as expected without forcing a user-source rewrite: installed migration and
normal journal ingestion are runtime proof; archive/rollback/rewrite behavior is
automated source proof, not a claimed installed-source rewrite experiment.
The comparison helper is the same `compare_counter_journal_upgrade.py`, now taking
the deployment backup directory name as an argument; aggregate output is
`compare_counter_journal_upgrade.v10-v11.json` under `.codex/temp/stage2/`.

## Remaining boundaries

This closes installed component support, advancing ingestion and the tested
refusal/recovery boundary. It does not close compressed-history recovery,
complete generation adoption/recovery, reconciliation, all untimed/all-time presentation
states or programme-wide user visual acceptance. Those remain under ING-002,
EVID-002/004 and SHIP-004. The quantity/retention definition in PROJECT.md explicitly
distinguishes these gaps from implemented behavior.
