# TajsTokens foundation

> **Status: authoritative working document.**
>
> During the architecture reset, this file is the sole authority for product/data architecture and planning. Existing code describes what the application currently does, not what it is required to mean. Documents under `docs/archive/` are historical context only.

## Why this reset exists

TajsTokens reached a point where implementation, UI, and derived metrics moved faster than our understanding of the underlying data. A polished result is not useful if the source semantics are uncertain.

The reset therefore starts from evidence. We will study each source empirically, record what it actually exposes, preserve uncertainty, and only then define normalized data and read models.

If current code conflicts with this document or with newly established source evidence, that conflict is a finding. We do not bend the evidence to preserve an existing implementation.

## Working principles

- **Evidence before interpretation.** A field existing does not prove what it means beyond what the source supports.
- **Unknown is a valid result.** Missing, ambiguous, delayed, or unexplained data must remain explicit.
- **Provenance is mandatory.** Normalized observations must retain where and when they came from and what source identity produced them.
- **Conflicting observations are data.** One source must not silently overwrite another merely because we prefer it.
- **Normalization must not invent semantics.** Conversion into a common shape may rename or type known fields, but may not manufacture relationships that have not been demonstrated.
- **Derived views are rebuildable.** Selection, aggregation, and policy belong above durable evidence and must be replaceable without rewriting history.
- **Privacy stays conservative during the reset.** Do not expand durable collection of prompts, reasoning text, source bodies, credentials, or other content-bearing payloads merely to make analysis easier.
- **Existing implementation has no grandfathered semantic authority.** Useful plumbing may survive; claims must earn their way back through source evidence.

## 1. Raw source acquisition

Work one source at a time. For each source, first establish how TajsTokens can read it and collect representative, sanitized examples across normal and awkward states.

At this layer we record what the source emitted. We do not calculate product metrics, reconcile it with other sources, or decide which source is "right".

Source acquisition must document at least:

- access mechanism and lifecycle;
- raw response/record shapes;
- timestamps and identifiers present in the source;
- optional/missing fields;
- observed variants across versions or runtime states;
- error, unavailable, partial, and stale-looking responses;
- whether the source is read-only from TajsTokens' point of view.

Raw content does not automatically belong in the durable database. Retention is a separate decision made per source after privacy and reprocessing needs are understood.

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

Every normalized observation must retain enough provenance to answer:

- which source produced it;
- which source record/object/window/session it came from, when such identity exists;
- when the source says it happened, if known;
- when TajsTokens captured it;
- which fields were actually present;
- which normalization contract/version produced the normalized form.

Normalization must preserve meaningful distinctions between sources. We will not force unrelated provider concepts into a universal schema merely because they look similar in a dashboard.

Two conflicting source observations may normalize into two conflicting observations. Resolving that conflict is not the normalizer's job.

## 4. Durable evidence store

The durable store is history of normalized evidence, not a warehouse of product conclusions.

Its design must support:

- deterministic observation identity and replay/idempotence where possible;
- preservation of distinct source observations;
- append/rebuild-friendly history;
- explicit source and normalization provenance;
- schema evolution without silently changing the historical meaning of a stored row;
- rebuilding read models after policies change;
- conservative handling of content-bearing or credential-bearing source material.

Fact/evidence tables must not contain values whose only justification is a current UI formula or interpretation policy.

If an existing database table cannot satisfy these constraints cleanly, compatibility with that table is not more important than getting the evidence model right. Migration, rebuild, or replacement remain valid options.

## 5. Read models

Read models turn durable evidence into answers the application can use now. They are derived, policy-driven, and replaceable.

A read model may select, group, or aggregate observations, but it must make its policy explicit. Where relevant, a result should carry or expose:

- evidence/source provenance;
- capture/observation time;
- coverage or scope;
- freshness;
- missing/ambiguous evidence;
- the policy/version used to choose among or combine observations.

A read model must never make a derived selection look like a raw provider fact. If two sources disagree, the policy may choose one for a particular view, but the underlying disagreement remains inspectable.

The first goal is trustworthy current facts and inspectable history. Predictive, inferential, cross-signal, and presentation work is outside the current design scope and must not constrain these five layers.

## Current work

There is deliberately no phase roadmap during the reset. Work proceeds by establishing source contracts and only then promoting proven semantics upward through the five layers above.

The first investigation should begin with the source behind the quota values currently shown in the app, because those values triggered the reset. The task is to capture and study its real responses over time, document the contract and unknowns, and avoid designing downstream calculations until that contract is trustworthy.

## Documentation rule

Do not create a competing roadmap, architecture guide, phase document, or semantic specification while this reset is active. Update this file instead.

Historical documents may be consulted for implementation archaeology, but any useful claim from them must be re-established and deliberately incorporated here before becoming current architecture again.
