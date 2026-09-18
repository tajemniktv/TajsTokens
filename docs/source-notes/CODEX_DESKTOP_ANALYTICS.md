# Installed Codex Desktop analytics acquisition — 2026-09-18

## Scope and evidence

Read-only static inspection of the running Windows Desktop installation, plus the two
user-supplied screenshots. No credentials, cookies, process memory, private conversation
stores or network response bodies were read. No authenticated request, UI injection,
configuration change or application restart was performed.

Installed package: `OpenAI.Codex_26.915.3509.0_x64__2p2nqsd0c76g0`.
Code archive: `app/resources/app.asar`, SHA-256
`8227F6234CF2CC418EC8BBDEEDEC03F8D777F85520929FF2D9D38E774F681DFD`.
The archive was indexed in place; only bounded matching source fragments were inspected.
This is exact installed Desktop code, not an assertion that public CLI source matches it.

Evidence anchors inside that archive (minified symbols are version-specific):

- `webview/assets/billing-analytics-41393e6c50dd.js`: plan history and top-chat queries.
- `webview/assets/queries-7823c72ca4f9.js`: account tokens, credits and tool metrics.
- `webview/assets/app-initial-f61fcec072b5.js`: shared HTTP client and host-service bridge.
- `.vite/build/main-CIvjSspu.js`: Desktop HTTP execution and authentication handling.
- `.vite/build/preload.js`: renderer-to-host MessagePort connection.
- `.vite/build/bootstrap-CqlvPvwP.js`: production API base configuration.

## Actual acquisition path

```text
Desktop analytics React query
  -> shared safeGet / safePost client
  -> host service httpFetch.fetch
  -> Desktop fetchWrapper.fetchHttp / performDesktopFetch
  -> authenticated HTTP to the configured OpenAI backend
```

The shared client exported as `Rrn` is imported as `Ce` in billing analytics and `r` in
the queries bundle. Its `safeGet`/`safePost` implementations reach `Don`, which calls
`$H.httpFetch.fetch`. Main creates that service with a callback to `fetchWrapper.fetchHttp`.
The production API base is `https://chatgpt.com/backend-api`; the routes below are relative
to that base, not publicly supported app-server RPC method names.

Desktop's HTTP wrapper obtains authentication through its app-server connection's
`getAuthToken`/cached-token methods, enforces allowed destinations and account identity,
and executes the HTTP request through `applicationNetwork.fetch` (or its progress path).
Thus the CLI/app-server participates in authentication, but the analytics HTTP requests
are Desktop-owned. This is not `account/usage/read` forwarding the reports.

The renderer host connection uses `connect-app-host`, a transferred MessagePort and the
Electron IPC channel `codex_desktop:connect-app-host`. Main constructs services scoped to
its web contents and disposes them when those contents are destroyed. This is an internal
Desktop bridge, not an established third-party HTTP/export interface. No supported external
analytics method was found in the inspected path or available Codex app tools. That is not
a proof that no other private interface exists. Attaching to/injecting into this bridge or
extracting authentication is not part of the approved integration boundary.

## Reports and semantics

| Surface | Desktop request | Evidence / limitations |
| --- | --- | --- |
| Plan history | GET `/wham/usage/plan_limit_history?days=7` | Feature-gated; ChatGPT auth and account/user identity required. Five-minute stale/refetch interval, no automatic retry; 404 becomes null. |
| Personal token history | GET `/wham/usage/daily-token-usage-breakdown` | `start_date`, `end_date`, `group_by=day`; account/user-scoped query, one-minute stale/refetch interval. |
| Consumer credits | GET `/wham/usage/credit-usage-events` | Separate credit report, not inferred subscription quota. Workspace routes differ. |
| Top chats | POST `/wham/usage/thread_usage/query_v2` | Batched thread groups, creation timestamps and descendant thread IDs; differs from the CLI's thread estimate route. |
| Skills | GET `/wham/analytics/daily-skill-usage-metrics` | Date range, day grouping, top-skill bound, `workspace_user=true`. |
| Plugins | GET `/wham/analytics/daily-plugin-usage-metrics` | Date range, day grouping, top-plugin bound, `workspace_user=true`. |

Plan-history parsing retains `data_as_of`, `coverage_start`, `coverage_complete`,
`approximate` (default true), `boundary_tolerance_seconds`, and period identity, window,
plan, boundaries, accounting completeness, nullable basis-point usage and breakdowns.
The Desktop view maps feature/model/surface/turn-start to the corresponding backend
dimensions. Its unavailable message covers missing usable report data or partial coverage
with no periods. Empty complete history and request errors have separate messages.
The screenshot therefore does **not** identify an exact HTTP status or prove a 404.

Top chats selects at most 100 roots, discovers subagent descendants, rejects overlapping
groups and bounds a request to 1,000 participating thread IDs. Its UI consumes
`five_hour_limit_percent`, `weekly_limit_percent`, and `balance_usage_credits`, with nullable
values remaining unavailable. Embedded UI descriptions distinguish chat-lifetime usage
relative to the **current full allowance** from purchased/granted credit debits. These are
not automatically the CLI's `estimatedUsageCreditsMicros`, nor historical period deltas.
The screenshot's failed chat panel does not prove this route is unsupported or that its
failure has the same cause as null app-server estimates. No response was captured here.

## Integration conclusion

The screenshots corroborate existing account activity and show additional Desktop analytics,
but not usable plan-history or top-chat values. Desktop has a richer private backend path;
there is no newly established supported acquisition seam for TajsTokens in this investigation.

Keep the current app-server collector and its `NoSupportedSeam` states. A Codex-owned,
allowlisted structured report/export or new app-server method would satisfy the user's
authentication boundary. Consuming the internal Desktop bridge is not equivalent to using
a supported API. No provider labels, account associations, quota-cost mapping, TT or live
forecast promotion change follows from static route discovery alone.
