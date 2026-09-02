# TajsTokens foundation

> **Status: authoritative working document.**
>
> During the architecture reset, this file is the sole authority for product/data architecture and planning. Existing code describes what the application currently does, not what it is required to mean. Documents under `docs/archive/` are historical context only.

## Why this reset exists

TajsTokens reached a point where implementation, UI, and derived metrics moved faster than our understanding of the underlying data. A polished result is not useful if the source semantics are uncertain.

The reset therefore starts from evidence. We will study each source empirically, record what it actually exposes, preserve uncertainty, and only then define normalized data and read models.

If current code conflicts with this document or with newly established source evidence, that conflict is a finding. We do not bend the evidence to preserve an existing implementation.

## Product direction: Codex-first observability

TajsTokens is currently a **Codex-first observability application**. The immediate goal is not broad provider support, a generic AI telemetry platform, or a lowest-common-denominator usage dashboard. The goal is to make Codex richly inspectable from trustworthy local evidence.

Codex exposes a large and evolving set of source-native concepts. Where source contracts establish their semantics, TajsTokens should preserve and use that richness rather than flattening it into generic `session`, `event`, or `usage` records merely because those names might also fit another provider later.

At the same time, TajsTokens should not become permanently shaped like Codex. Possible future providers influence the architecture through clean boundaries, not through premature genericization.

The guiding rule is:

> **TajsTokens is Codex-first, not Codex-shaped. Provider-native semantics are preserved. Shared abstractions are projections, not storage requirements. A provider is never forced into another provider's ontology merely to obtain a unified UI.**

This means:

- Codex may have a rich first-class model for concepts that Codex actually exposes, such as threads, turns, items, relationships, tools, jobs, logs, catalog state, or other contracted source-native structures.
- A future provider may have a completely different first-class model, such as runs, requests, generations, workers, queues, hardware observations, or concepts we have not encountered yet.
- Shared infrastructure may unify provenance, capture time, observation identity, contract/version metadata, persistence mechanics, and query plumbing without requiring a universal semantic payload.
- Shared product views may project different provider-native models into a common answer only where the relevant semantics have been demonstrated to overlap.
- Provider-specific views are allowed, and often preferable, when a provider exposes useful information that has no honest cross-provider equivalent.
- Future-provider support is a design constraint on seams and ownership, not current implementation scope.

A useful mental model is:

```text
Codex sources
    -> Codex source contracts
    -> Codex-native normalized observations
    -> durable evidence
    -> rich Codex read models
                 \
                  -> optional shared observability projections
    -> Codex-specific UI

Future provider sources
    -> provider-specific source contracts
    -> provider-native normalized observations
    -> durable evidence
    -> provider-native read models
    -> provider-specific UI
                 \
                  -> compatible shared projections where semantics genuinely overlap
```

There is deliberately no universal provider schema in the middle of this pipeline.

## Working principles

- **Evidence before interpretation.** A field existing does not prove what it means beyond what the source supports.
- **Unknown is a valid result.** Missing, ambiguous, delayed, or unexplained data must remain explicit.
- **Provenance is mandatory.** Normalized observations must retain where and when they came from and what source identity produced them.
- **Conflicting observations are data.** One source must not silently overwrite another merely because we prefer it.
- **Normalization must not invent semantics.** Conversion into a stable shape may rename or type known fields, but may not manufacture relationships that have not been demonstrated.
- **Preserve provider-native richness.** Do not discard a safe, understood source-native field or relationship merely because the current UI does not render it or a hypothetical future provider may not have it.
- **Shared abstractions must be earned.** Similar names or shapes are not enough. A shared concept is introduced only when its observable semantics are sufficiently equivalent for the read-model question being answered.
- **Derived views are rebuildable.** Selection, aggregation, classification, and policy belong above durable evidence and must be replaceable without rewriting history.
- **Native state and derived presentation state are distinct.** If a read model later classifies source-native states into broader categories, the source-native value remains evidence and the broader category remains explicit policy.
- **Local visibility is not durable collection.** TajsTokens is local-first and should let a user inspect the data their local Codex installation exposes, including content-bearing values where appropriate. Showing a source value on-device does not by itself justify copying it into TajsTokens' durable evidence store.
- **Export is a separate privacy boundary.** If TajsTokens exports source data, the export path must make the data leaving the local inspection boundary explicit and should support sanitization/redaction where appropriate. Raw and sanitized export modes, if both exist, must be distinguishable rather than silently changing the evidence.
- **Privacy stays conservative for duplication and secrets.** Do not expand durable duplication of prompts, reasoning text, source bodies, credentials, authentication material, or other sensitive payloads merely to make analysis easier. Secret-bearing data requires explicit handling even when the source can be inspected locally.
- **Existing implementation has no grandfathered semantic authority.** Useful plumbing may survive; claims must earn their way back through source evidence.
- **Do not design for hypothetical provider symmetry.** A clean provider boundary is valuable; forcing Codex into a lowest-common-denominator model is not.

## 1. Raw source acquisition

Work one source at a time. For each source, first establish how TajsTokens can read it and collect representative examples across normal and awkward states.

Local inspection may expose raw source values to the user. Fixtures, bug reports, documentation examples, and other artifacts intended to leave the user's machine must be sanitized or deliberately reviewed before sharing.

At this layer we record what the source emitted. We do not calculate product metrics, reconcile it with other sources, or decide which source is "right".

Source acquisition must document at least:

- access mechanism and lifecycle;
- raw response/record shapes;
- timestamps and identifiers present in the source;
- optional/missing fields;
- observed variants across versions or runtime states;
- error, unavailable, partial, and stale-looking responses;
- whether the source is read-only from TajsTokens' point of view.

Raw content does not automatically belong in the durable database. Retention is a separate decision made per source after privacy, product value, and reprocessing needs are understood.

## 2. Source contracts

A source becomes usable by the architecture only after it has an empirical source contract.

A source contract records:

- what has been directly observed;
- field names, types, units, and value ranges where known;
- identity semantics: what makes two observations the same or different;
- time semantics: event time, capture time, reset time, update time, or unknown;
- update/mutation behavior observed over repeated reads;
- relationships between fields that are demonstrated by evidence;
- known limitations and coverage boundaries;
- unresolved questions and hypotheses, explicitly labelled as such;
- sanitized fixtures or reproducible probes supporting the contract.

A source contract must distinguish **known**, **observed but unexplained**, and **hypothesized**. Hypotheses do not become normalization rules merely because they are convenient.

### Source-contract template

```text
Source:
Acquisition method:
Read/write posture:
Representative samples:

Observed fields:
Observed variants:
Identity semantics:
Time semantics:
Update/mutation behavior:
Coverage:

Known:
Observed but unexplained:
Hypotheses to test:
Unknown:

Fixtures/probes:
Open questions:
```

## 3. Normalized observations

Normalization converts source-specific evidence into stable TajsTokens observations only where the source contract justifies the conversion.

**Normalized does not mean generic.** The preferred normalized form is usually provider-native and source-aware. A Codex state-thread observation, Codex turn observation, rollout observation, or app-server observation may remain explicitly Codex-specific if that is the honest semantic model.

Every normalized observation must retain enough provenance to answer:

- which provider/source produced it;
- which source record/object/window/thread/turn or other native identity it came from, when such identity exists;
- when the source says it happened, if known;
- when TajsTokens captured it;
- which fields were actually present;
- which source contract and normalization version produced the normalized form.

Shared observation infrastructure may provide a common envelope for provenance, timing, identity, and versioning. It must not require unrelated providers to share the same payload schema.

Normalization must preserve meaningful distinctions between sources and between provider-native concepts. We will not force unrelated concepts into a universal schema merely because they look similar in a dashboard.

Two conflicting source observations may normalize into two conflicting observations. Resolving that conflict is not the normalizer's job.

## 4. Durable evidence store

The durable store is history of normalized evidence, not a warehouse of product conclusions and not an automatic mirror of every readable source payload.

Its design must support:

- deterministic observation identity and replay/idempotence where possible;
- preservation of distinct source observations;
- provider/source-native identities and relationships where established by contract;
- append/rebuild-friendly history;
- explicit source and normalization provenance;
- schema evolution without silently changing the historical meaning of a stored row;
- rebuilding read models after policies change;
- conservative handling of content-bearing or credential-bearing source material.

The evidence store may contain provider-specific tables or structures when that preserves contracted semantics cleanly. A single generic table is not inherently more architectural than several honest provider-native structures.

Fact/evidence tables must not contain values whose only justification is a current UI formula, cross-source selection policy, inferred classification, or other derived interpretation.

A source field being useful for local inspection does not automatically make it appropriate for durable duplication. Durable retention of content-bearing values must be justified independently from the ability to display those values from the source on demand.

If an existing database table cannot satisfy these constraints cleanly, compatibility with that table is not more important than getting the evidence model right. Migration, rebuild, or replacement remain valid options.

## 5. Read models

Read models turn durable evidence into answers the application can use now. They are derived, policy-driven, and replaceable.

TajsTokens may have two kinds of read models:

1. **Provider-native read models.** These may be rich and provider-specific. For Codex, they can join contracted evidence into useful Codex concepts and relationships without pretending those concepts are universal.
2. **Shared observability projections.** These may project multiple provider-native models into a common answer such as current activity or recent failures, but only where the required semantics genuinely overlap.

A provider-native read model may combine multiple contracted sources for the same provider. Any source selection, reconciliation, grouping, or field precedence is read-model policy and must be explicit and inspectable. Conflicting evidence remains preserved underneath the selected view.

A shared projection is never a storage requirement. For example, a future Codex thread and a future local inference run might both project into a broad activity summary while remaining completely different first-class models in evidence and provider-specific views.

Where relevant, a read-model result should carry or expose:

- evidence/source provenance;
- capture/observation time;
- provider-native identity;
- coverage or scope;
- freshness;
- missing/ambiguous/conflicting evidence;
- the policy/version used to choose among, classify, or combine observations.

A read model must never make a derived selection or broad presentation classification look like a raw provider fact.

Provider-specific UI is allowed to be substantially richer than shared UI. TajsTokens should exploit Codex data where it is trustworthy instead of hiding useful information because another provider may not expose an equivalent.

The first goal is trustworthy current facts and inspectable history. Predictive, inferential, cross-signal, and presentation work is outside the current design scope and must not constrain these five layers.

## Current work

There is deliberately no phase roadmap during the reset. Work proceeds by establishing source contracts and only then promoting proven semantics upward through the five layers above.

The current product focus is **rich Codex observability**. We are inventorying and studying Codex-owned sources such as state SQLite, rollout JSONL, and app-server surfaces one source at a time. The recently added Codex State DB Explorer is acquisition/investigation tooling for that work, not a semantic shortcut around source contracts.

Current work should therefore favor:

- discovering what Codex exposes directly;
- capturing representative source states and changes over time;
- documenting source-native identities, relationships, timing, lifecycle, and uncertainty;
- preserving useful Codex richness when contracts justify it;
- building durable Codex evidence only after those contracts are credible;
- deriving rich Codex read models from that evidence without turning them into a compulsory schema for future providers.

Possible future providers should be kept in mind by maintaining clean provider/source boundaries and avoiding Codex assumptions in shared infrastructure. We are not implementing hypothetical providers now, and we are not weakening the Codex model to make imaginary future mappings easier.

Quota is one Codex-observability domain among many rather than the organizing principle of the architecture.

## Codex state SQLite inspection boundary

TajsTokens includes a read-only **Codex State DB Explorer** as an acquisition and investigation surface. It is deliberately separate from product read models: the explorer returns the selected source path, SQLite object definitions, `PRAGMA table_info`/index metadata, row counts, and paged raw values without assigning domain meaning to any field.

Source: Codex private SQLite files discovered as `state_*.sqlite` under `CODEX_HOME`, `.db`/`.sqlite` files under `CODEX_HOME/sqlite`, and explicitly configured or development-local snapshot folders (the checked-out `private` folder is one such snapshot folder).

Acquisition method: enumerate candidates, explicitly select one file, open with SQLite `Mode=ReadOnly` and `PRAGMA query_only=ON`, then query `sqlite_master`, table/index pragmas, counts, and raw rows. The workbench can also inspect a selected pair of source instances/snapshots and export a schema-only view that keeps each source path and schema fingerprint separate.

Read/write posture: TajsTokens must not mutate the selected Codex file. Baseline snapshots and row fingerprints are held in memory only. Local inspection may display raw values from the selected source, including content-bearing fields where the user chooses to inspect them. If an export surface is provided, it must clearly distinguish what is being exported and should provide sanitization/redaction before data leaves the local boundary where appropriate; raw export, if supported, must be an explicit user choice rather than an accidental side effect. SQLite may create or refresh transient WAL shared-memory sidecars while reading a live WAL database; those are an SQLite read-path effect, not content writes by TajsTokens.

Known: schema shape, table names, column declarations, indexes, row counts, and the values returned by the read-only queries are source observations.

Observed but unexplained: table names and fields may vary with Codex versions, migrations, or runtime state. Each object receives a deterministic schema fingerprint from its raw SQLite definition/columns/indexes, and each inspection receives a database-level fingerprint composed from those object fingerprints. Compact databases use in-memory row hashes to report same-count row changes. Large databases (over 64 MiB) or tables over 100,000 rows intentionally use a bounded row-count fingerprint so inspecting content-heavy snapshots remains responsive; all fingerprints are an inspection aid rather than a semantic change classification. The source-native key trace performs exact, parameterized `column = value` lookups independently in each matching table/view (for example, `thread_id`) and reports raw matches with their source path; it does not join tables or infer relationships.

Unknown: field meaning, lifecycle guarantees, identity semantics, and update ordering remain unknown until separately supported by captured source evidence. Discoveries in this explorer do not become normalized TajsTokens observations automatically.

## Documentation rule

Do not create a competing roadmap, architecture guide, phase document, or semantic specification while this reset is active. Update this file instead.

Historical documents may be consulted for implementation archaeology, but any useful claim from them must be re-established and deliberately incorporated here before becoming current architecture again.
