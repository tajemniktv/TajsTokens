# Stage 4: frozen joint TT basis and score history

## Definition, normalization and compatibility

`TtJointWorkloadBasis` defines `tt-joint-basis/v1` independently of the existing
`TtWorkloadBasis` / `tt-category-basis/v1` research contract. The older evaluator,
basis IDs and archived research payloads retain their original format and behavior.

The new ruler names model/category weights, an explicit reference basket, active
coordinate dimensions, convention, weight unit, vintage and immutable source
snapshot references. Model is always active. Effort, executed tier, product and
context band can be active or deliberately outside the scoring convention. A weight
must name every active dimension; an unknown active label never acts as a wildcard.
Ignoring an observed dimension is an explicit property of that ruler, not inferred
provider equivalence. A future standard-speed reference convention could therefore
ignore executed speed without claiming that speed was observed or billed standard.

The supported space is sparse. A missing weight means unsupported; an explicit zero
is a known zero weight. The five categories retain their disjoint meanings. The
reference basket must have a finite positive weighted sum and fully supported
positive amounts. Its score is one TT. Decimal arithmetic is used for defined
amounts/weights; overflow withholds scoring rather than inventing a zero.

Identity covers the entire definition, dimensions, weights, basket and parent
identity. Canonical ordering, duplicate reference aggregation and invariant decimal
normalization make equivalent ordering, numeric scale and culture produce the same
SHA-256 identity (`tt-joint:` prefix). Input arrays are copied and exposed read-only.
Unsupported contracts, duplicate weight coordinates, negative quantities, unsupported
positive reference amounts and overlarge vocabularies are rejected.

An explicit additive extension retains old weights, old support and the exact old
basket/normalizer, adds source references and gets a new identity linked to its
parent. The compatibility check verifies those properties; a parent label alone
does not prove compatibility. Existing supported workloads score identically under
an approved extension. A newly supported model/category can acquire a new score,
while its originally recorded unsupported result remains intact.

## Durable scoring output

The existing `SqliteTelemetryRepository` owns the new typed archive. Intelligence
schema 8 adds `tt_joint_score_snapshots` transactionally; telemetry, observatory,
state-index schemas and the legacy `tt_evaluation_snapshots` format are unchanged.
No native Codex store is written. The normal backup is required for restoring a
binary that only supports the earlier intelligence schema.

`tt-joint-score-snapshot/v1` saves the selected basis, closed numeric joint workload,
selection interval, dataset capture time, save time, provider/profile scope and its
exact score (or explicit unavailable `null`). It only accepts observed increments,
not conditional forecast composition. Saving an ID again is idempotent for exactly
the same output; a conflicting output is rejected without replacing the original.
This archive is bounded to 512 KiB per snapshot and 20 history entries per read.
Its allowlist contains no prompts, code, credentials or raw source payloads.

A restatement is a new snapshot with an explicit link to an intact existing score.
It must retain that score's captured workload, capture time and selection/scope;
changed input observations require a separate original result. Original and restated
scores keep their own frozen basis and numeric output across restart. New prices,
model discovery and date changes do not call this writer, replace a basis or activate
a new one. No automatic restatement policy is introduced.

Reads verify payload bounds/checksum, format, saved basis identity/normalizer, score
consistency and archive-column metadata. Invalid records remain visible as
unavailable entries without hiding intact neighbors. A failed migration does not
advance the schema marker; retry after repairing the obstruction is supported.

## Delivery boundary

This slice implements **SCORE-002** and the exact complete-workload primitive needed
by SCORE-003. It uses synthetic test rulers, not approved public coefficients. There
is no active runtime TT basis, pricing refresh or billing/remaining-quota conversion.
T-01 / DEC-001 still owns the actual public schedule, basket and speed convention;
DEC-005 still owns its activation/restatement policy. SCORE-003 will add explicit
bounded/partial/inconsistent results for question-specific missing quantities.
No quota or workload forecasting algorithm changes in this slice.

## Validation and installation

The full Core suite passed **798 tests** (1m49s). Coverage includes canonical identity across order,
decimal scale and culture, basket normalization/additivity, immutable arrays,
definition changes, unknown versus zero weights, active versus ignored dimensions,
overflow/inconsistent workloads, explicit compatible extensions, restart/retry/
conflict behavior, restatement scope/input identity, malformed neighbors, refreshed
checksums with inconsistent saved identities, payload bounds and migration rollback.
The unchanged research-basis and legacy archive tests also passed.

The default Windows build passed with zero warnings/errors (44.81s), installed and
restarted the daily application at 2026-10-28 10:06:14 UTC. Installed and probe-loaded
assemblies matched SHA-256:

- Core: `C1762A5E00060FA9E673FE4948AA747E4A9684DA338AADBE2EA7F36A0DAFD46B`
- Infrastructure: `2FA7E3FBE2FA84ECA97E9718C07B8B059C204299C751BD504A722E2F2F3218C4`

The upgrade check confirmed intelligence 7 → 8; final read-only backup/live comparison
confirmed all **six existing research snapshots byte-for-byte unchanged** and an
empty new joint archive. All 96,391 native rows and 435 original counter-input journal rows retained their
numbers and chronology; no removals occurred. Telemetry/observatory/state-index
versions remained 16/11/8. The final backup was `20261028T100551389Z-ba3ea718` under the
normal deployment retention policy.

A probe using the installed assemblies wrote only to a disposable fixture database.
It saved an original synthetic score, retried idempotently, saved an explicitly linked
extension restatement, reopened both records and rejected an identity conflict.
The extension preserved the original 1.5 TT score and normalizer. An additional
malformed normalizer with a refreshed checksum was returned as unavailable while
both intact records remained readable. No synthetic score
or basis was written into daily data; public activation and visual acceptance remain
separate work.
