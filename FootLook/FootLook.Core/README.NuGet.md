# FootLook.Core

FootLook.Core is a **zero-interaction API observability** library for ASP.NET Core. It transparently captures incoming HTTP requests/responses through middleware, stores them in-memory and/or on disk, and exposes a set of operational endpoints (plus an optional live dashboard) so you can inspect traffic, performance, and failures without changing your business code.

## Features

- **Live request capture** streamed in real time as traffic happens
- Zero-interaction: no changes to your controllers or business code
- In-memory and file-based storage (no database required)
- Real-time push over SignalR + in-process capture events
- Request/response body capture with size limits
- Path ignoring, sampling, and method filtering
- Targets .NET 8

## Install

```bash
dotnet add package FootLook.Core
```

## Quick start

```csharp
using FootLook.Core.Extensions;
using FootLook.Core.Options;

var builder = WebApplication.CreateBuilder(args);

// 1. Register FootLook services (this also registers SignalR for the live feed)
builder.Services.AddFootLook(options =>
{
	builder.Configuration.GetSection("FootLook").Bind(options);
});

var app = builder.Build();

// 2. Add the capture middleware early in the pipeline
app.UseFootLook();

// 3. Map the FootLook API, the live hub (/footlook/live) and the dashboard (/footlook.html)
var footlookOptions = app.Services.GetRequiredService<FootLookOptions>();
app.MapFootLookEndpoints(footlookOptions);

app.Run();
```

That's all. Run your API and open **`/footlook.html`**: the live dashboard ships inside the package, so there are no files to copy and no `UseStaticFiles()` to add. Its sign-in page (`/footlook-login.html`) and the "Connect to site" script (`/footlook-connect.js`) are served the same way. FootLook never captures its own API or dashboard requests, so none of them need to be in `IgnoredPaths`.

The pages hold no data: everything they show comes from the authenticated `/footlook` API. To run FootLook without the dashboard, set `options.EnableDashboard = false`.

## Live captures

FootLook is built around **live, real-time observability**. As soon as `UseFootLook()` is in the pipeline, every request flowing through your API is captured and made available instantly — no polling, no manual instrumentation, no database.

### Stream captures over SignalR

`MapFootLookEndpoints` maps the live hub at `{EndpointBasePath}/live` (default `/footlook/live`), behind the same sign-in as the rest of the API. Connect any SignalR client to it to receive captures the moment they happen. Browsers pass the bearer token as the `footlook_token` query parameter, because a WebSocket upgrade can't carry an `Authorization` header:

```javascript
const connection = new signalR.HubConnectionBuilder()
	.withUrl("/footlook/live?footlook_token=" + encodeURIComponent(token))
	.withAutomaticReconnect()
	.build();

connection.on("captureReceived", capture => {
	console.log(
		`[LIVE] ${capture.method} ${capture.path} ` +
		`${capture.statusCode} ${capture.durationMs}ms`
	);
});

await connection.start();
```

### Subscribe to captures in-process

You can also react to captures directly in your app (logging, alerting, metrics):

```csharp
var events = app.Services.GetRequiredService<FootLook.Core.Services.CaptureEvents>();

events.OnRequestCaptured += capture =>
{
	Console.WriteLine(
		$"[LIVE] {capture.Method} {capture.Path} " +
		$"{capture.StatusCode} {capture.DurationMs}ms");
};
```

Each capture includes details such as HTTP method, path, status code, duration, correlation id, timestamp, and (optionally) request/response bodies.

## Configuration

FootLook is configured through `FootLookOptions`. You can bind from configuration and/or set values inline:

```csharp
builder.Services.AddFootLook(options =>
{
	builder.Configuration.GetSection("FootLook").Bind(options);

	options.Enabled = true;
	options.SamplingRate = 1.0;                 // 1.0 = capture everything

	options.CaptureRequestBody = true;
	options.CaptureResponseBody = true;
	options.MaxBodyLength = 1024 * 1024;        // 1 MB cap per body

	options.EndpointBasePath = "/footlook";     // base route for endpoints
	options.QueCapacity = 10_000;               // internal capture queue size

	options.ServiceName = "MyApi";
	options.EnvironmentName = builder.Environment.EnvironmentName;

	// Paths that should never be captured (FootLook's own API and dashboard
	// are always skipped, so they don't need listing)
	options.IgnoredPaths.Add("/swagger");
	options.IgnoredPaths.Add("/favicon.ico");
	options.IgnoredPaths.Add("/.well-known");

	// A path ending in "*" is a wildcard prefix match (e.g. "/robots*" also
	// catches "/robots.txt" and scanner variants like "/robots933456.txt");
	// without it, "/robots" only ever matches the exact path "/robots" or
	// "/robots/..." - not "/robots.txt". Recommended baseline for any
	// internet-facing deployment, to keep bot/crawler/scanner noise out of
	// your captures:
	options.IgnoredPaths.Add("/robots*");
	options.IgnoredPaths.Add("/sitemap*");
	options.IgnoredPaths.Add("/ads.txt");
	options.IgnoredPaths.Add("/humans.txt");
	options.IgnoredPaths.Add("/security.txt");
	options.IgnoredPaths.Add("/browserconfig.xml");
	options.IgnoredPaths.Add("/apple-touch-icon*");
	options.IgnoredPaths.Add("/xmlrpc.php");
	options.IgnoredPaths.Add("/wp-");   // wp-login.php, wp-admin, wp-content...
	options.IgnoredPaths.Add("/.git");
	options.IgnoredPaths.Add("/.env");

	// HTTP methods FootLook will capture (not related to CORS - this filters which
	// requests get observed, it does not affect cross-origin browser permissions)
	options.AllowedMethods.Add("GET");
	options.AllowedMethods.Add("POST");
	options.AllowedMethods.Add("PATCH");
	options.AllowedMethods.Add("PUT");
	options.AllowedMethods.Add("DELETE");
});
```

### Options reference

| Option | Description | Typical value |
|--------|-------------|---------------|
| `Enabled` | Master switch for capturing. | `true` |
| `SamplingRate` | Fraction of requests to capture (`0.0`–`1.0`). | `1.0` |
| `CaptureRequestBody` | Capture request bodies. | `true` |
| `CaptureResponseBody` | Capture response bodies. | `true` |
| `MaxBodyLength` | Max bytes stored per body. | `1048576` |
| `EndpointBasePath` | Base route prefix for FootLook endpoints. The dashboard calls whatever prefix you set. | `/footlook` |
| `EnableDashboard` | Serve the built-in dashboard at `/footlook.html` (plus `/footlook-login.html` and `/footlook-connect.js`). | `true` |
| `QueCapacity` | Capacity of the internal capture queue. | `10000` |
| `ServiceName` | Logical service name shown in captures. | `"MyApi"` |
| `EnvironmentName` | Environment name shown in captures. | `Development` |
| `IgnoredPaths` | Paths excluded from capture, in addition to FootLook's own API and dashboard. | swagger, bot/scanner paths |
| `AllowedMethods` | HTTP methods FootLook will capture; empty list means all methods. Not related to CORS. | `GET`, `POST`, ... |

### Example `appsettings.json`

```json
{
  "FootLook": {
	"Enabled": true,
	"SamplingRate": 1.0,
	"CaptureRequestBody": true,
	"CaptureResponseBody": true,
	"MaxBodyLength": 1048576,
	"EndpointBasePath": "/footlook",
	"QueCapacity": 10000,
	"ServiceName": "MyApi"
  }
}
```

## Central sign-in

By default a FootLook host keeps its own developer accounts. Instead, you can let people sign in on FootLook's website: register your API as a project there, copy its **ProjectId**, and add it to your host:

```csharp
builder.Services.AddFootLook(options =>
{
	builder.Configuration.GetSection("FootLook").Bind(options);
	options.Central.ProjectId = "prj_yourprojectid";   // or set it in configuration, below
});
```

```json
{
  "FootLook": {
    "Central": { "ProjectId": "prj_yourprojectid" }
  }
}
```

That is all the setup there is:

- **No secret is involved.** The ProjectId is a public label (it is visible in the browser when your dashboard redirects to sign-in). Nothing from FootLook has to be stored in your app.
- **The host fetches the public key by itself.** It downloads FootLook's public signing key (`{Issuer}/.well-known/jwks.json`), caches it for about an hour, refreshes it when it meets an unknown key id (at most every 30 seconds) and keeps using the last good copy if the service is briefly unreachable. Before the first successful download `POST /footlook/auth/exchange` answers `503 central_unavailable`.
- **Your captures never leave your API.** After signing in, the browser is sent back to your dashboard with a short-lived, single-use pass. Your host checks its signature, issuer (must be FootLook's), audience (must be exactly your ProjectId) and expiry, then starts its normal local observation session. Captures stay in your host's memory for that session and are deleted when it ends.
- Only the project's owner and the members they invited can get a pass. The owner is the FootLook admin of the session; members are ordinary users.
- **Local accounts are switched off** while a ProjectId is set: `POST /footlook/auth/register`, `/auth/login` and `/auth/microsoft` answer `404 { "error": "disabled_in_central_mode" }`. Remove the ProjectId to get them back.
- `GET /footlook/auth/config` tells the dashboard which mode the host is in. Optional overrides (`Issuer`, `JwksUrl`, `LoginUrl`, `ClockSkewSeconds`) exist under `FootLook:Central` for a self-hosted or local central service; they default to FootLook's own.

The built-in dashboard (`/footlook.html`) handles central sign-in with no extra setup: it redirects a signed-out browser to the sign-in page and accepts the pass when the browser comes back.

## Endpoints (internal)

`MapFootLookEndpoints(...)` registers a small set of endpoints under `EndpointBasePath` (default `/footlook`) that **power the live dashboard**. You typically don't call these directly — they exist so the dashboard and live view have data to render (health, captures list, stats, recent/history, clear, and pause/resume). The primary way to consume FootLook is the **live stream** and **in-process events** shown above.

## Storage

By default FootLook uses:

- **In-memory sink** — fast, queryable store backing the live view.
- **File sink** — persists captures to disk.

No database is required. Data is combined through a composite sink and processed by a background worker draining the capture queue.

**Known limitation: single-instance only.** The capture queue, in-memory store,
and request-deduplication cache are all in-process state. Behind a load
balancer with multiple instances/pods, each instance sees only its own slice
of traffic, with no unified view and no cross-instance deduplication.
Multi-instance support would need an external shared store and a SignalR
backplane (e.g. Redis) and isn't implemented yet — treat FootLook as a
single-instance/single-host tool for now.

## Notes

- Targets .NET 8.
- Add `UseFootLook()` early in the pipeline so it can observe the full request lifecycle.
- Add your own tooling paths (swagger, favicon) to `IgnoredPaths` to avoid capture noise. FootLook's own API and dashboard are skipped automatically.
- The capture queue is bounded (`QueCapacity`) and drops the oldest queued
  capture under sustained overload rather than blocking request threads or
  growing unbounded. Drops are counted and visible via
  `GET {EndpointBasePath}/reliability/status` (`QueueDropCount`) so silent
  data loss under load is at least observable.
