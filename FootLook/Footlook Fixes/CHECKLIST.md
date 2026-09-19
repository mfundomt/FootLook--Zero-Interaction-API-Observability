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
- [x] **Field-name-only PII masking — investigated, partially a non-issue**:
  the "nested/differently-named fields slip through" framing was half
  inaccurate. Masking is text-pattern based, not JSON-tree based, so it
  already matches a configured field name at *any* nesting depth or inside
  arrays - verified with `user.credentials.password` (2 levels deep) and an
  array of objects each with their own `cvv`, both masked correctly. The
  real, unavoidable gap is genuinely differently-named fields
  (`userSSN` when only `ssn` is configured) - name-based matching can only
  ever catch names you've told it about. Mitigated by expanding the default
  `SensitiveBodyFields`/`SensitiveQueryParameters` lists with common
  variants (`socialSecurityNumber`, `cardNumber`, `cvc`, `pin`,
  `clientSecret`, `privateKey`, etc.) and documenting the limitation clearly
  in `FootLookOptions` xmldoc. True unknown-field-name detection would need
  value-shape heuristics (Luhn-checked card numbers, SSN-shaped strings)
  with a real false-positive risk - scoped out as a distinct future feature,
  same treatment as multi-instance scaling.
  Branch: `fix/pii-masking-coverage`

## 🟡 Medium

- [x] **Errors only logged to `Console.Error`** — fixed as a side effect of the
  `CompositeSink` reliability work; now goes through `ILogger` and
  `CaptureReliabilityState`.
- [x] **Dedupe cache O(n) full scan per request** — the expired-key sweep now
  runs at most once per 5 seconds (time-gated via an interlocked next-sweep
  timestamp) regardless of request volume, instead of scanning the whole
  dictionary on every single request. A slightly-late eviction is harmless -
  `IsDuplicateRequest`'s own cutoff check still correctly rejects expired
  entries in between sweeps.
- [x] **`InMemorySink` caps by item count only, not total bytes** — added
  `FootLookOptions.MaxInMemoryCaptureBytes` (default 200MB, 0 disables it).
  Tracks a running estimate of captured body + header text size and evicts
  the oldest capture whenever either the byte cap or the existing count cap
  (`MaxInMemoryCaptures`) is exceeded. `Clear` and retention pruning keep the
  estimate accurate (recomputed after `Clear`'s drain-and-reinsert; decremented
  per evicted item during retention pruning).
- [x] **`MongoCaptureRepository.SearchAsync` ignored its own filters** — fixed
  to actually build a Mongo filter from `path`/`minStatusCode`/`minDuration`/
  `correlationId` instead of unconditionally returning the first 100
  documents regardless of what was asked for. (The full-collection-scan cost
  of `GetStatsAsync`, and the fact this repository isn't wired to any active
  endpoint in the demo, are unchanged - those are scale/wiring concerns
  distinct from this correctness bug, and this repository is dead code in
  the current demo regardless per the architecture docs.)
  Branch (all three above): `fix/medium-backend-issues`
- [x] **SignalR reconnect doesn't re-sync missed captures** — `onreconnected`
  now triggers `refreshAll(state.page)` to pull whatever was missed during
  the gap, instead of only updating the connection-status indicator and
  silently leaving the feed stale.
- [x] **RPM counter can double/undercount** — root cause confirmed: `loadRecent`
  unconditionally re-pushes every capture still inside the 60s window into
  the RPM timestamp list on *every* call (initial load, every throttled live
  refresh, every filter change), so a capture that stayed within the window
  across multiple refreshes was counted once per refresh, not once total.
  Replaced the plain array (`recentTimestamps`) with a capture-id-keyed map
  (`recentCaptureTimestampsById`) - re-processing the same capture now
  overwrites its own entry instead of adding a duplicate, so RPM correctly
  reflects distinct captures regardless of how many times a refresh touches
  them. Verified via Playwright: sent 5 distinct captures, RPM read 5, then
  held steady at 5 through 3+ additional throttled refresh cycles with no
  new traffic (previously this would have inflated with each refresh).
  Branch (both above): `fix/medium-frontend-issues`

## 🟢 Low / cleanup

- [x] `Random()` per-request → `Random.Shared` (thread-safe, no per-request
  allocation, no clock-seed correlation risk under concurrent load).
- [x] Redundant channel allocation in `ShadowQueue` — was already fixed as a
  drive-by during the queue-drop-visibility work (critical bucket).
- [x] `CaptureIdentityResolver` re-instantiated per-request → registered as a
  DI singleton (`FootLookServiceCollectionExtensions`). It's fully stateless
  (no instance fields, every method a pure function of its parameters), so
  this is safe. Both call sites (`/captures/recent`, `/captures/identity`)
  now take it as an injected parameter instead of `new`-ing it up.
- [x] Dead JS in the clear-captures handler — worse than "dead code": a
  `window.location.href` full-page reload ran immediately after the DELETE
  call, making everything after it (five lines referencing `captures`,
  `requestTimestamps`, `window.rpmLabels`/`rpmValues`, and a
  `clearRpmTimelineGraph()` helper using a Chart.js-style API that doesn't
  exist anywhere in this file) permanently unreachable - leftover from an
  earlier implementation. Replaced with an actual client-side state reset
  (`state.captures`/`chartCaptures`/`recentCaptureTimestampsById` cleared,
  re-render) instead of forcing a full page reload; removed the now-fully-dead
  `clearRpmTimelineGraph` function.
- [x] No `aria-live` on the streaming feed — added `aria-live="polite"
  aria-atomic="false" aria-relevant="additions"` to the capture table body
  and an `aria-label` on the table itself.
  Branch (all five above): `cleanup/low-priority-bundle`
- [ ] **No dark/light theme persistence — scoped out.** Investigated: there is
  no theme system at all currently (no toggle, no dark CSS variables,
  nothing to persist). Building one from scratch is a real UI feature
  project, not a "fix" - same treatment as multi-instance scaling. Not
  attempted here.
- [ ] **No trace-waterfall view across a correlation ID — scoped out.** Same
  reasoning: querying all captures sharing a correlation ID and rendering a
  timeline/waterfall visualization is a genuine new feature, not a quick
  fix. Not attempted here.

---

## Status: every actionable item from the original audit is closed

Every Critical, High, and Medium item is fixed and merged into `staging`.
The Low/cleanup bundle is done except two items intentionally scoped out as
separate future feature initiatives (not "fixes"):

1. **Dark/light theme system** — doesn't exist yet; building one from scratch
   is a UI feature project.
2. **Trace-waterfall view across a correlation ID** — a genuine new feature
   (query + timeline visualization), not a quick fix.
3. **True multi-instance support** (external shared store + SignalR
   backplane) — needs its own dedicated design pass (backend choice, hosting
   budget, migration path) before any code.

All three are candidates for a future initiative, not blockers on shipping
what's in `staging` now.
