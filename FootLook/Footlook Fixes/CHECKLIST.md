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
- [x] **Queue-level drops now visible** — `DropOldest` evictions were
  completely silent before; `ShadowQueue` now detects (best-effort, under
  concurrent writers) when a write evicts the oldest queued capture and
  records it via `CaptureReliabilityState.RecordQueueDrop`. Surfaced as
  `QueueDropCount` on `GET {EndpointBasePath}/reliability/status`, and folded
  into the existing `EventLossRatePercent` SLO calculation alongside persist
  failures. Drive-by fix: removed `ShadowQueue`'s redundant double channel
  allocation (a field initializer that was immediately overwritten in the
  constructor).
  Branch: `fix/queue-drop-visibility`
- [x] **Durability/horizontal scaling — direction decided, not implemented**:
  true multi-instance support (external shared store + SignalR backplane)
  is explicitly scoped OUT of this fix pass as its own future initiative
  needing dedicated design (backend choice, hosting budget, migration path).
  FootLook is documented as single-instance-only for now
  (`README.NuGet.md`, `FootLookOptions.QueCapacity` xmldoc) rather than
  silently implying otherwise. The cheap, isolated durability win (queue-drop
  visibility, above) was done; the expensive one (external store, cross-
  instance dedupe, SignalR backplane) was deliberately not attempted here.
- [x] **Unbounded body buffering / no streaming support** — turned out to be
  bigger than the one-line finding suggested: FootLook fully buffered *every*
  response in memory before the client received a single byte, regardless of
  size or content type (SSE, chunked, large downloads all silently broke).
  Replaced the buffer-then-copy-at-the-end `MemoryStream` with
  `CappedTeeStream`, which streams every write straight to the client
  immediately while mirroring at most `MaxBodyLength` bytes into a bounded
  side-buffer for capture. Also fixed the request-body side: reads are now
  capped at `MaxBodyLength` characters during the read itself instead of
  reading the whole body then truncating the resulting string. A real bug
  surfaced and was fixed during testing: the initial truncation-detection
  approach (`StreamReader.EndOfStream`) does a synchronous read under the
  hood, which Kestrel disallows by default and crashed with 500 on any body
  that hit the cap - replaced with a one-char async probe read.
  Verified: TTFB on a 5-chunk streamed response is ~3ms vs ~2.5s total (was
  previously indistinguishable from total time); a 2MB response is delivered
  to the client in full while the capture is correctly truncated to exactly
  `MaxBodyLength` bytes + marker; a 2MB POST body no longer crashes and is
  captured truncated the same way; masking, exception-path capture, and
  normal traffic all still work.
  Branch: `fix/streaming-and-bounded-body-buffering`
- [x] **Dashboard fan-out entirely client-side, no debouncing** — every
  `captureReceived` event used to trigger its own full REST re-fetch (stats +
  paged list) and full table re-render, one-to-one. Replaced with a
  throttle (not a naive resetting debounce, which could starve indefinitely
  under continuous traffic): incoming captures queue up, and at most one
  refresh fires per 300ms window covering however many arrived in it.
  Verified with a 20-event burst through a real browser: REST calls to
  `/captures/stats` dropped from 20 (one-to-one) to 8, while all 20 captures
  still show up correctly (`Total Requests: 20` on the dashboard) - throttled,
  not lost. (Scope-based SignalR groups from the critical fix already cut the
  blast radius across dashboards; this fixes the same-dashboard, high-volume
  re-render cost.)
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

1. Remaining High-bucket items that don't need a product decision: unbounded
   body buffering/streaming, dashboard debouncing, field-aware PII masking.
2. Medium bucket.
3. Low/cleanup bundle — small enough to batch into one pass at the end.
4. (Future, separate initiative) True multi-instance support — external
   shared store + SignalR backplane. Needs its own dedicated design pass,
   not part of this checklist.
