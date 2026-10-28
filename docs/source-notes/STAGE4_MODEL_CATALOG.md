# Stage 4: model discovery without entitlement assumptions

## Source and retained contract

`codex-model-catalog/v1` is a closed projection of the Codex home's
`models_cache.json`, acquired read-only through the existing native-source gateway.
The parser keeps model/display names, known aliases, declared reasoning efforts and
default, input modalities, service-tier IDs, context capacities, API-listing flag,
visibility, positive capability flags and explicitly reported successor/retirement
metadata. Source fields absent from this contract remain unknown. The installed
cache supplies no aliases; a successor is stored separately and is not an alias.

No root identity, ETag, instructions, model/effort/tier descriptions, migration text,
unknown payloads or raw source JSON are retained. The source file is bounded to
4 MiB, the catalog to 512 models and nested label lists to 32. Reads use read-only
file access, support cancellation and return explicit absent/invalid/unavailable
states with fixed diagnostics. No model endpoint, credential file, API key or paid
request is introduced for discovery.

The cache's native fetch/revalidation time stays separate from actual collection
start/end. File modification time does not become source time. Client version is
the cache's declared writer version; it does not establish the identity/version of
every installed Codex instance. The source contract is corroborated by the read-only
Codex clone at `7498521d288b9b3b96ffba4eedf089d8d6e06a84`, particularly
`models-manager/src/cache.rs` and `protocol/src/openai_models.rs`. This is schema
corroboration rather than a claimed exact source match to the installed binary.
Runtime shape and bounded field parsing supply the installed evidence.

## Versioning, collection and recovery

The immutable definition ID covers source/adapter contract, declared client version
and normalized allowlisted metadata. Row/label order does not change identity;
changed capabilities, aliases or retirement metadata do. Caller arrays are copied.
Identical definitions coalesce while preserving first collection/source times and
the latest actual collection/source times. Freshness clocks do not manufacture a
new functional definition or overwrite its numeric metadata.

Intelligence schema 9 adds the definitions archive and latest read-attempt state
under the existing telemetry repository. Native stores and accounting schemas are
unchanged. Changed definitions remain retained. Absent/invalid later reads preserve
the last good metadata and expose the failed attempt separately. Product queries
read the archive without initializing/writing it. They inspect at most 20 versions,
512 KiB per normalized definition and explicitly report partial/invalid history;
older versions remain stored. Malformed records do not hide intact neighbors.

Capture runs inside the existing supplementary server-evidence collection cadence,
before remote acquisition. Local metadata can be retained even if that acquisition
fails. Metadata failures do not stop remote collection or canonical history. There
is no new background loop or per-quota-poll model request. A failed schema migration
does not advance its version marker, and repaired retry is supported.

## Meaning in the product

Planning offers cached and observed model names/efforts as suggestions while keeping
exact free-text input. Previously unseen models and efforts remain ingestible and
usable as requested history filters; metadata never gates their numeric accounting.
Current cached entries, historical catalog entries and local observations stay
distinct. Missing from the newest catalog does not establish retirement. Hidden
entries remain retained and can still appear through actual selected observations.
Explicit retirement dates remain source reports, not executed access checks.

**Every model choice currently says account access is unverified.** `supported_in_api`,
a picker listing, a reported access program, a previous observation or the current
quota account cannot establish present model entitlement. No supported positive
access-verification source is used by this slice. A future proof must supply its own
account, scope and time instead of upgrading a cached listing into verification.

Advanced Evidence shows source/collection times, metadata read state, history bounds,
efforts and reported retirement/successor facts. The product manifest advances to
`codex-intelligence/v3-model-catalog`. Model metadata does not select prices, provide
TT weights, activate a ruler or alter any forecast eligibility/training policy.

The explicit `--model-catalog <app-owned-db> <from-utc> <to-utc>` evaluation command
returns the same catalog/selected-observation projection as aggregate typed JSON.
It reads app-owned data, makes no source/credential request and accepts a past range
of at most 366 days. It is useful when distinguishing a missing cache collection,
unlisted observed name and actual metadata parse/retention failure.

## Validation

The full Core suite passed **815 tests** (1m48s). New tests prove closed-field/privacy
projection, unchanged source bytes/mtime, canonical immutable IDs, original collection
times through coalescing, retained retirement/successor metadata, absent versus
reported retirement, unknown observed models/efforts, unverified access, corrupted
neighbor recovery, read-only missing-table behavior, migration rollback and local
capture/backoff despite remote failure. The product-engine test also proves that
unavailable catalog history does not block selected native accounting.

The Windows build passed with zero warnings/errors (55.54s), installed/restarted the
daily app at 2026-10-28 10:49:57 UTC, and its collector retained one 11-model definition
at 10:50:04.957 UTC. Native source fetch/revalidation was 10:48:16.414 UTC. The cache
declared client **0.165.0**, while the separately invoked PATH CLI reported **0.164.0**;
these remain distinct observations, with no inferred cache producer/account.
The definition ID was
`codex-models:c0fd93dfc0a1aec26bb13d47e1827e8907b28398cd81c64a28124106eeac5fc9`.

The installed read-only catalog command over the fixed September 27–October 27 range
returned 11 choices, five observed model names, two hidden catalog entries, zero
known aliases/reported retirements, no invalid or truncated definitions, and access
unverified for every choice. This verified installed collection/retention/projection;
the interaction follow-through below and user visual acceptance are separate gates.

All 96,437 prior native rows, 481 counter-input journal rows and six existing research
snapshots survived the upgrade unchanged; one normal native/journal row arrived
after restart. Telemetry/observatory/state-index versions stayed 16/11/8; intelligence
advanced 8 → 9. The daily joint-TT archive remained empty.

Installed SHA-256 values for that catalog milestone:

- Core: `8DADE89CC2D2C0360BF2530F42CAD6397A6CBC3E64C366F095E656FBDF47A202`
- Infrastructure: `3C7900EB39ACE60E33CF58F0A224A17034A12197B166D57AC6D88D98AAF71AC6`
- Evaluation: `7CD36D50873087F13BDE750EEACC76FE74758757B7994217B3F44FB88AE79EC6`

### Installed picker and hidden-dashboard follow-through

The October 28 interaction pass verified that typing `gpt-6.1` opens the cached
Terra/Sol suggestions, keyboard selection retains the exact name, and effort
suggestions include `xhigh`/`high`. Selecting Terra reports cached catalog and local
observation while keeping account access unverified. An unlisted exact name is
kept as entered with its explicit unverified-access label. No model task or access
probe was submitted.

This exposed a lifecycle integration gap: `PlanModel` and `PlanEffort` had become
`AutoSuggestBox` controls, but `DashboardInputs.Capture` did not handle that type
and would throw during the 30-second hidden release. The existing helper now saves
and restores their text without retaining controls, suggestions or read models.
An installed full hide/reopen retained the Planning tab, `gpt-6.1-terra` and `xhigh`;
Diagnostics confirmed one hidden dashboard release pass in the same process.
A second full hide/reopen retained `unlisted-catalog-check` and
`custom-effort-check`, keeping the unlisted/unverified label; Diagnostics reached
two release passes. Quota polling and native collection remained live. Temporary
inputs were cleared after the checks. This proves text retention through actual
view rebuilding, without inferring account entitlement from either kind of name.

The full Core suite passed **831 tests** (1m49s); the zero-warning Windows build
passed (55.68s) and restarted the daily app at 2026-10-28 12:07:42 UTC. Installed app
DLL SHA-256: `D1E67B23D14B6C67B85570EEE48A56B2B8FEA4A9EE902CE8562F7D85EF40CE16`.
These are interaction and lifecycle checks, not user visual acceptance, aggregate
RAM-budget acceptance or completion of programme-wide SHIP-004. An unrelated
Windows Security overlay was left untouched throughout.
