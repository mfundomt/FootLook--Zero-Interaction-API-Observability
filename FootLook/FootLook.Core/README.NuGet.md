# FootLook.Core

FootLook.Core provides request capture middleware and observability endpoints for ASP.NET Core APIs.

## Install

```bash
dotnet add package FootLook.Core
```

## Quick start

```csharp
builder.Services.AddFootLook(options =>
{
	builder.Configuration.GetSection("FootLook").Bind(options);
});

app.UseFootLook();

var footlookOptions = app.Services.GetRequiredService<FootLookOptions>();
app.MapFootLookEndpoints(footlookOptions);
```

## Notes

- Targets .NET 8.
- Includes privacy masking, reliability telemetry, and operational endpoints.
