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

## Compressed rollout ingestion (ING-002)

Plain `.jsonl` and `.jsonl.zst` siblings are one discovery slot, with plain preferred.
The optional native state catalog can point to either representation, including a
plain path whose compressed sibling is the only file present. State-index schema 8
clears only disposable fingerprints/cursors to revisit previously skipped sources.

One retained physical handle supplies independent verifier/parser positions via
`RandomAccess`, so a rename cannot make them read different files. Compressed
sources deny in-place writes while held, permit rename/delete, and validate every
frame/checksum through EOF before any ingestion writes. A change in the selected
physical representation is retried rather than treated as a verified checkpoint.
The line reader and preflight share the 64 MiB record limit; Zstandard window log
25 caps each decoder window at 32 MiB. Input/output buffers are 64 KiB. No decoded
rollout or raw source JSON is written to a temporary file or evidence table.

Telemetry schema 16 adds nullable checkpoint coordinate kind, logical source path,
physical encoding and physical identity. Legacy plain checkpoints gain the decoded
coordinate kind only after full consumed-prefix SHA-256 verification. Compressed
resume decodes/discards from zero; compressed physical size is never used as the
decoded offset/EOF. The original UTF-8 bytes, including CR/LF and optional BOM,
remain in coordinates/hashes; the BOM is excluded only from transient JSON text.

Verified sibling/archive adoption retains occurrence IDs and original captures.
Archive aliases require a matching canonical filename and source-root namespace,
then the complete consumed prefix. A bounded alias search fails explicitly if
ambiguous; new occurrence identities include a hashed host/store namespace plus
physical identity. Existing IDs are preserved, not retroactively relabelled.
The original logical locator remains stable through moves, so reducer state and
later retirement cover all prior increments. Legacy moves that already changed the
file registry's locator are handled by known source-record generation membership,
not only current file labels: native/counter/context/timeline facts are preserved
and retired together. A regression reproduces the former missing-archive failure.
Equal filename/header/prefix data in
a fork or another source root does not establish the same occurrence.

Complete blank lines advance the checkpoint without becoming observations. An
unterminated final record remains pending. A valid empty replacement selects an
empty generation and preserves the old evidence before retiring its projections.
Missing-during-open results cannot register an unverified replacement identity.
Typed failure counts distinguish missing/inaccessible, truncated/invalid frames,
unsupported windows/dictionaries/checkpoints, oversized records and changed or
conflicting representations. Cancellation propagates; a missing source is not EOF.

The read-only reconciliation experiment also uses decoded bounds and the retained
handle. Its v7 report chooses one sibling rather than stopping at compressed file
size or counting both physical representations. It remains an aggregate experiment,
never a canonical importer or proof of cross-file billing identity. The separate
on-demand 2 MiB byte-comparison inspector remains conservative/plain-only; it must
not be used to claim complete compressed lineage or collection coverage.

Automated cases cover both persistence lanes, plain/compressed/both/restore/append,
archive and genuine rewrite, legacy checkpoint adoption, source-root separation,
inherited child prefixes, Unicode/CRLF/BOM, blank/incomplete tails, empty/corrupt/
truncated/checksum failures, concatenated/skippable frames, window/dictionary limits,
oversized records before writes, cancellation, atomic replacement and state-index
EOF/fingerprint behavior. Strong prefix verification and original capture facts
remain assertions, not inferred from successful parsing.

Installed decoder probes use synthetic frames generated directly into compressed
files with a 2 MiB window. They compare 16 and 256 MiB decoded sizes, record private
commit/working set and allocations without forced GC, and cancel a large validation
after warmup before proving handle release and a complete retry. This measures
compressed preflight only, not full ingestion/parser memory or the main/worker
aggregate background budget; PERF-001 remains open.

The final **747-test** Core suite passed (1m48s), including the legacy locator-
membership regression. The Windows build passed with zero warnings/errors
(1m25.32s) and normal deployment restarted PID 31612. Installed infrastructure
SHA-256: `A5911DDE30C9CEF7DAC773CB052B201F40370408C6C68BD6AE310C3FDF6B821C`.
The published `ZstdSharp.dll` SHA-256 is
`B285BB9F59928BECB8E048BF2A75E872F6555F8773D7B72386A9F77F447F425C`.
Both the port's MIT notice and Zstandard 1.5.7 BSD notice were verified byte-for-
byte against repository notices in the installed `ThirdPartyNotices/` directory;
they are also embedded in the infrastructure assembly.

The installed decoder's synthetic preflight results were:

| Decoded input | Encoded bytes | Wall time | Sampled process private peak | Allocated bytes |
|---|---:|---:|---:|---:|
| 16 MiB | 1,560 | 69.02 ms | 17.08 MiB | 210,080 |
| 256 MiB | 24,600 | 209.04 ms | 16.52 MiB | 238,936 |

The 10 ms sampler obtained 5/13 samples respectively; working-set peaks were about
44.51/44.52 MiB. These are separate console processes including runtime overhead,
not decoder-only allocation totals or full-app limits. The large validation was
also cancelled after warmup; an exclusive read-handle check then succeeded and a
fresh read completed all 268,435,456 decoded bytes. Reproduction uses the temporary
`compression-probe` project, `decoder-fixtures`, `decoder-16m-result.json`,
`decoder-256m-result.json` and `decoder-cancel-result.json` under `.codex/temp/stage2/`.

An initial installed comparison preserved 96,073 native rows and 117 journal rows
after the first compressed-capable build. Normal backup retention later rotated
that earlier backup; its aggregate comparison remains recorded. The final build
was compared read-only with its own normal pre-deployment backup: **96,120** native
rows and **164** complete journal rows had zero removals or changes to original
amounts/chronology/input payloads. Four new rows of each kind were collected normally.
The installed markers were telemetry 16, observatory 11 and state index 8.

During that final comparison, decoded-coordinate checkpoints advanced from 405 to
434 and compressed checkpoints from 374 to **403**; all recorded retirement counts
remained zero. This is real installed prefix-verified adoption without restamping
retained facts. The remaining 64 legacy checkpoint rows are not a count of missing
files: they can include aliases, sources outside the current catalog or pending
work. No corpus-completeness percentage or cause distribution is inferred from them.
The final aggregate output is
`compare_counter_journal_upgrade.v11-v11.base16-base16.json`; native Codex stores
were read only, and the comparison wrote only aggregate local diagnostics.

## Response/provider reconciliation (EVID-002)

The app's current command resolution selects the installed npm CLI, which reports
`codex-cli 0.164.0`. Its own `app-server generate-json-schema --experimental`
output confirms `account/usage/read`, nullable `threadId`, and the response's
separate summary, daily buckets and optional thread estimate. Neither request nor
response has cursor/page/limit fields. Thread estimate credits are required micros;
USD micros and grouped native token amounts remain nullable. Rate-limit metadata
separately retains purchased balances and spend-control fields. These are separate
quantities and overlapping snapshots, not inputs to one summed amount.

The generated response/parameter schema SHA-256 values are:

- `GetAccountTokenUsageResponse.json`:
  `A58E58C852C888E5DE925D99A66688D55EBB2FFF038E795AADF04F8A8426DFF8`.
- `NullableGetAccountTokenUsageParams.json`:
  `A992C6EA0FE02B0E9F46B18AFB51B8EE2635E7F0C7D546F71E7F95E2A98BEBBA`.
- `ClientRequest.json`:
  `503D09A12AE36FCA152E044877D22E93B0094C2D08C3A53ED731F3C8CF33B9CE`.

The inspected source clone is still `7498521d288b9b3b96ffba4eedf089d8d6e06a84`;
it corroborates the method shape but is not claimed as the installed binary's
exact source revision. Installed schema and retained captures provide the version-
matched evidence. No new authenticated backend probe was needed for this check.

At the read-only retained-data sample (`2026-10-28T07:30:15Z`), CLI 0.164.0 had 113
available account-activity captures and 678 unavailable thread estimates. The 791
usage observations had before/after account bracket timestamps; 226 adapter no-
seam markers did not. A matching bracket remains `ServerCorrelated`, never native
account identity embedded in a usage response. The optional backend adapter's own
reports/errors keep their distinct contract and capability state. The app-server
adapter no longer claims that missing implementation proves a method absent from
every installed/future client or backend.

All-null activity now remains `Unavailable`; an explicitly empty day list can be
`Empty` without claiming account-wide zero/completeness. Non-token summary fields
such as a reported turn duration or streak remain available evidence. Existing
stored observations are not rewritten to this newer interpretation.

New app-server batches retain a closed selection scope: collection ID, actual
selection time, requested thread UUIDs, examined candidate rows, eligible UUIDs,
candidate truncation, policy and bounds. Selection examines at most 100 recent
native session groups, requests at most six UUIDs, uses known source activity in
the preceding 30 days and rotates least recently fetched threads first. Unknown
role/model/account do not delete their token amounts or become invented account
ownership. Automatic attempts are separated by at least 30 minutes in-process;
startup/manual requests can occur sooner. This is not the quota polling cadence
or complete thread/response coverage. Legacy captures retain unknown selection
scope. Reusing an observation ID with changed scope fails the existing immutable-
observation check; invalid scope is rejected without losing prior facts.

The installed post-build collector retained one nine-observation batch with six
requested threads, 68 examined/eligible candidates, no candidate truncation, and
the recorded 30-day/30-minute policy. Results remained one available account
report, six unavailable thread estimates and two adapter no-seam markers. No
missing estimate was converted into zero.

The response store sample retained 30,716 occurrences: 30,628 active and 88 retired.
All active rows had complete reported lineage, three complete numeric snapshots
and source time; no within-source repeated/conflicting logical keys were found.
All active source IDs in that sample predated host/store namespacing. This does not
invent a native account/host namespace for old observations or establish global
billing identity. The thread evidence readout now exposes active response snapshot
completeness, turn/thread snapshot presence, reported lineage and unknown time.
Turn/thread snapshots remain cumulative alternatives; no quantity is added to the
native reducer ledger by this display or comparison.

The installed aggregate reconciliation CLI now discovers compressed files too.
Its read-only v7 run examined **439** stable rollouts, with zero changed/failed
files and 90,546 owned token observations. Among 29,187 response records, 28,993
single-pending response/legacy vector pair candidates matched exactly; 194 response
records were unpaired at lifecycle boundaries, and 61,707 legacy records had no
pending response. No repeated/conflicting response-key or invalid-vector counter
was emitted. These are scoped, overlapping diagnostic counts, not proof of native
pair identity or authority to discard all legacy counters. They justify preserving
the current ledger and retaining the supplemental evidence rather than summing
response/turn/thread/legacy representations. The current-file and retained-store
denominators differ; equality/completeness across them is not assumed.

The full **757-test** Core suite passed (1m44s). Tests cover scope/cadence/rotation,
candidate truncation and denominators, missing metadata with retained amounts,
scope immutability/legacy absence/corrupt display, unknown versus empty activity,
and overlapping response snapshots with retired/untimed/missing-lineage cases.
The Windows build passed with zero warnings/errors (58.06s), installing/restarting
PID 4348. Installed infrastructure SHA-256:
`9A40FEF704B5429FBEB8EC325C1CEC3EFEF111502E20B4E55E7E97BD188A135C`.

Computer Use reached the installed Evidence tab, but a Windows Security passkey
prompt reappeared when the app received focus. No authentication/security action
was automated. The sampling-text visual pass remains unconfirmed; the app was
activated for inspection and was not subsequently minimized through that prompt.
This is separate from the successful stored-scope and headless runtime checks.

Reproduction artifacts under `.codex/temp/stage2/` are generated metadata in
`app-server-schema-0.164.0/`, `audit_retained_response_provider.py`,
`retained-response-provider-audit.json`, and `native-response-reconciliation.json`.
They contain schemas or aggregate diagnostics, not source payloads, account keys or
thread/response IDs. Native files/credentials stayed read-only; no opt-in backend
setting was enabled or fresh experimental backend request made by these audits.

## Eligibility explanations (EVID-003)

The [threshold-study follow-up](FORECAST_THRESHOLD_STUDY.md#eligibility-follow-up-evid-003)
records the shared production/research selector, explicit stage units/denominators,
overlapping input and training reasons, numeric examples and installed reproduction.
The named later frozen dataset reproduces the 67/116 workload exclusions as 63
origins without retained dated token events and four with only later-collected
events; none is an unknown-role-only exclusion. The other 49 origins fail duration
support after passing training, rather than failing session-count support. The
historical v1 and later fixture identities remain distinct.

The 762-test Core suite and Windows build passed; the build installed/restarted the
daily app. No source data/schema, threshold or selected model changed. Historical
physical discovery/decode coverage cannot be inferred from this retained forecast
dataset and stays unknown in its report. ING-002's acquisition evidence remains
separate, as do EVID-004's all-time/untimed quantity presentation and SHIP-004's
user visual acceptance. See the linked record for exact hashes, times and limits.

## Remaining boundaries

This closes installed component support, advancing ingestion and the tested
refusal/recovery boundary and the supported compressed-ingestion cases above.
It does not prove a complete native corpus was available/recovered, complete
cross-source reconciliation, all untimed/all-time presentation states or programme-
wide user visual acceptance. Those remain under EVID-004 and SHIP-004.
The quantity/retention definition in PROJECT.md explicitly
distinguishes these gaps from implemented behavior.
