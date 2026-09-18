# FootLook Fix Checklist

Tracks the audit findings in `fixes.txt` through to resolution, in the sequence
agreed on ("My read on sequencing" at the bottom of that file, plus decisions
made since). Update this file's checkboxes as items land — treat it as the
source of truth for what's left, not `fixes.txt` (that file is the frozen
original audit).

Legend: `[x]` done and merged into `staging` · `[ ]` not started · `[~]` partially done

---

## 🔴 Critical (root-caused into 2 fixes + 1 isolated bug)

- [x] **Scope isolation across all read endpoints + live feed** — `CaptureScopeId` now
  set by the middleware and enforced on every capture read/delete route and the
  SignalR hub (scoped groups), not just 2 of 6 routes.
  Branch: `fix/capture-scope-isolation`
- [x] **Unscoped `DELETE /footlook/captures`** — resolved by the same fix above;
  delete is now scoped to the caller.
- [x] **`CompositeSink` swallowing sink failures** — per-sink retry + failures now
  recorded via `CaptureReliabilityState`/`ILogger` instead of silently
  disappearing into `Console.Error`.
  Branch: `fix/sink-failure-reliability`
- [x] **Middleware corrupting the response on exception** — no longer forces
  `StatusCode=500` or writes a body before rethrowing; host's own exception
  handling now runs untouched.
  Branch: `fix/middleware-exception-handling`

## Not in the original audit, added by explicit request

- [x] **Authentication** — was entirely open before. Built as login + short-lived
  bearer tokens (not static per-request API keys), with an admin tier for
  host-wide actions (pause/resume, privacy-audit clear, self-heal/setup).
  Swagger's Authorize button wired to the same scheme.
  Branch: `feature/bearer-token-authentication`

## 🟠 High

- [x] **`FileSink` full directory scan on every write** — cleanup now only runs
  right after an actual rotation, not on every capture.
- [x] **Masking regex recompiled per request** — compiled once per field, cached
  for the app's lifetime.
  Branch (both above): `fix/filesink-scan-and-masking-perf`
- [~] **Two disconnected pause/resume mechanisms** — the demo's duplicate,
  unauthenticated `CaptureEvents`-based `/pause`/`/resume` and the dead
  `/footlool/pause` typo route were removed as part of the auth work.
  `CaptureRuntimeState` is now the only pause mechanism. *Not separately
  verified as its own item — worth a quick confirmation pass, not a new fix.*
- [x] **CORS "dead config"** — turned out to be a misreading, not a real gap:
  there is no CORS middleware, policy, or `app.UseCors()` anywhere in the
  codebase to be "half-wired." The finding was based on a wrong comment
  sitting above `FootLookOptions.AllowedMethods`, which is a real, fully
  functional, unrelated option (filters which HTTP methods FootLook
  *captures*, via `ShadowMiddleware.ShouldCaptureMethod`). Decision: same-
  origin only, no CORS needed (the dashboard is served from the same host as
  the API). Fixed the misleading comment/docs in `Program.cs` and
  `README.NuGet.md` instead of "removing" config that was never actually
  CORS-related.
  Branch: `fix/mislabeled-cors-comment`
- [ ] **No durability** — in-memory-only queue, no crash recovery, queue-level
  drops (`DropOldest`) not even counted. **Needs a direction-setting
  conversation** (durable queue? accept the tradeoff and just add drop
  metrics?) before coding.
- [ ] **No horizontal scaling story** — queue, dedupe cache, in-memory store are
  all per-instance singletons; breaks behind a load balancer with multiple
  pods. **Same conversation as durability** — likely resolved together (e.g.
  an external store) or explicitly scoped out as "single-instance only" for
  now.
- [ ] **Unbounded body buffering / no streaming support** — bodies fully read
  into memory before `MaxBodyLength` truncation applies; SSE/large downloads
  get fully buffered instead of streamed through.
- [ ] **Dashboard fan-out entirely client-side, no debouncing** — every live
  event triggers a full REST re-fetch + full table re-render; will choke at
  real production volume. (Scope-based SignalR groups already cut the blast
  radius as a side effect of the critical fix, but the debounce/batch problem
  itself is untouched.)
- [ ] **Field-name-only PII masking** — separate from the recompilation fix
  above. Only exact configured field names are masked; nested objects,
  arrays, or differently-named fields (`userSSN`, `customer.ssn`) still slip
  through. Needs schema-aware (JSON-structural) masking, not just caching.

## 🟡 Medium

- [x] **Errors only logged to `Console.Error`** — fixed as a side effect of the
  `CompositeSink` reliability work; now goes through `ILogger` and
  `CaptureReliabilityState`.
- [ ] **Dedupe cache O(n) full scan per request** to expire old entries.
- [ ] **`InMemorySink` caps by item count only, not total bytes** — large bodies
  can still balloon memory even under the count cap.
- [ ] **SignalR reconnect doesn't re-sync missed captures** — dashboard can
  silently show a gap without knowing it.
- [ ] **RPM counter can double/undercount** across reloads and reconnects
  (client-local, not server-computed).
- [ ] **`MongoCaptureRepository` is dead weight** — pulls entire collections into
  memory, `SearchAsync` ignores its own filters, not wired to any active
  endpoint.

## 🟢 Low / cleanup (bundle, not yet started)

- [ ] `Random()` per-request instead of `Random.Shared`
- [ ] Redundant channel allocation in `ShadowQueue`
- [ ] `CaptureIdentityResolver` re-instantiated per-request instead of DI singleton
- [ ] Dead JS referencing undefined globals in the clear-captures handler
- [ ] No dark/light theme persistence
- [ ] No trace-waterfall view across a correlation ID
- [ ] No `aria-live` on the streaming feed (accessibility)

---

## What's next (in order)

1. **Durability/scaling conversation** — needs your input on deployment target
   (single instance vs. multi-instance) before any code.
2. Remaining High-bucket items that don't need a product decision: unbounded
   body buffering/streaming, dashboard debouncing, field-aware PII masking.
3. Medium bucket.
4. Low/cleanup bundle — small enough to batch into one pass at the end.
