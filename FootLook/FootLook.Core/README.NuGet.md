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

// 1. Register FootLook services
builder.Services.AddFootLook(options =>
{
	builder.Configuration.GetSection("FootLook").Bind(options);
});

// 2. Enable live capture streaming
builder.Services.AddSignalR();

var app = builder.Build();

// 3. Add the capture middleware early in the pipeline
app.UseFootLook();

// 4. Map the FootLook endpoints (these back the live dashboard)
var footlookOptions = app.Services.GetRequiredService<FootLookOptions>();
app.MapFootLookEndpoints(footlookOptions);

// 5. Map the live hub so captures stream to connected clients
app.MapHub<FootLook.Core.Hubs.CaptureHub>("/footlook/live");

app.Run();
```

## Live captures

FootLook is built around **live, real-time observability**. As soon as `UseFootLook()` is in the pipeline, every request flowing through your API is captured and made available instantly — no polling, no manual instrumentation, no database.

### Stream captures over SignalR

Connect any SignalR client to the live hub to receive captures the moment they happen:

```csharp
builder.Services.AddSignalR();

// after building the app:
app.MapHub<FootLook.Core.Hubs.CaptureHub>("/footlook/live");
```

Example browser client:

```javascript
const connection = new signalR.HubConnectionBuilder()
	.withUrl("/footlook/live")
	.withAutomaticReconnect()
	.build();

connection.on("RequestCaptured", capture => {
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

> **Tip:** If the live dashboard does not show up at runtime, navigate to `/footlook.html`.

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

	// Paths that should never be captured
	options.IgnoredPaths.Add("/footlook");
	options.IgnoredPaths.Add("/footlook.html");
	options.IgnoredPaths.Add("/swagger");
	options.IgnoredPaths.Add("/favicon.ico");
	options.IgnoredPaths.Add("/.well-known");

	// HTTP methods allowed for CORS on the FootLook endpoints
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
| `EndpointBasePath` | Base route prefix for FootLook endpoints. | `/footlook` |
| `QueCapacity` | Capacity of the internal capture queue. | `10000` |
| `ServiceName` | Logical service name shown in captures. | `"MyApi"` |
| `EnvironmentName` | Environment name shown in captures. | `Development` |
| `IgnoredPaths` | Paths excluded from capture. | dashboard/swagger paths |
| `AllowedMethods` | HTTP methods allowed via CORS on endpoints. | `GET`, `POST`, ... |

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

## Endpoints (internal)

`MapFootLookEndpoints(...)` registers a small set of endpoints under `EndpointBasePath` (default `/footlook`) that **power the live dashboard**. You typically don't call these directly — they exist so the dashboard and live view have data to render (health, captures list, stats, recent/history, clear, and pause/resume). The primary way to consume FootLook is the **live stream** and **in-process events** shown above.

## Storage

By default FootLook uses:

- **In-memory sink** — fast, queryable store backing the live view.
- **File sink** — persists captures to disk.

No database is required. Data is combined through a composite sink and processed by a background worker draining the capture queue.

## Notes

- Targets .NET 8.
- Add `UseFootLook()` early in the pipeline so it can observe the full request lifecycle.
- Add capture-related paths (dashboard, swagger, favicon) to `IgnoredPaths` to avoid self-capture noise.
