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

## Remaining boundaries

This closes installed component support, advancing ingestion and the tested
refusal/recovery boundary. It does not close compressed-history recovery,
superseded-generation retention, reconciliation, all untimed/all-time presentation
states or programme-wide user visual acceptance. Those remain under ING-001/002,
EVID-002/004 and SHIP-004. The quantity/retention definition in PROJECT.md explicitly
distinguishes these gaps from implemented behavior.
