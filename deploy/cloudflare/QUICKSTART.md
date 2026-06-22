# 🚀 Quick Start Guide for Developers

Want to try FootLook on your own API? Follow this 5-minute guide!

---

## Step 1: Add FootLook to Your .NET API

### Install Package (from source)

```bash
# Clone the FootLook repository
git clone https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability.git

# Add project reference to your API
dotnet add reference path/to/FootLook.Core/FootLook.Core.csproj
```

### Configure in Program.cs

```csharp
using FootLook.Core.Extensions;
using FootLook.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// Add your existing services...
builder.Services.AddControllers();

// 1. Add FootLook
builder.Services.AddFootLook(options =>
{
	options.CaptureRequestBody = true;
	options.CaptureResponseBody = true;
	options.ServiceName = "MyAPI";
	options.Enabled = true;
});

// 2. Add SignalR
builder.Services.AddSignalR();

// 3. Configure CORS (important!)
builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy =>
	{
		policy.SetIsOriginAllowed(origin => true)
			  .AllowAnyMethod()
			  .AllowAnyHeader()
			  .AllowCredentials();
	});
});

var app = builder.Build();

// 4. Use CORS before other middleware
app.UseCors();

// 5. Use FootLook middleware
app.UseFootLook();

// 6. Map FootLook endpoints
app.MapFootLookEndpoints();

app.MapControllers();
app.Run();
```

---

## Step 2: Run Your API

```bash
dotnet run
```

Your API should now be running with FootLook enabled at:
- API: `http://localhost:5000`
- FootLook endpoints: `http://localhost:5000/footlook/*`

---

## Step 3: Connect to Dashboard

### Option A: Use Deployed Dashboard (Easiest)

1. Go to: **https://footlook.pages.dev/dashboard.html**
2. In the Backend Configuration panel:
   - Enter: `http://localhost:5000`
   - Click "Test Connection"
   - Click "Connect"
3. Start making API calls and watch them appear!

### Option B: Use Local HTML File

1. Download `dashboard.html` from the repository
2. Open it directly in your browser (file://)
3. Configure backend URL as above

---

## Step 4: Generate Some Traffic

### Using Swagger
```
http://localhost:5000/swagger
```

### Using curl
```bash
curl http://localhost:5000/api/your-endpoint
```

### Using your frontend application
Just use your API normally!

---

## What You'll See

The dashboard shows in real-time:
- ✅ Every HTTP request/response
- ⏱️ Response times
- 📊 Status codes
- 📝 Request/response bodies
- 🔍 Headers and metadata
- 📈 Requests per minute chart
- 🎯 Top endpoints analytics

---

## Troubleshooting

### "Connection failed" Error

**Check CORS:**
```csharp
// Make sure this is BEFORE app.UseFootLook()
app.UseCors();
```

**Check FootLook is registered:**
```csharp
builder.Services.AddFootLook(options => { /* ... */ });
app.UseFootLook();
app.MapFootLookEndpoints();
```

**Check endpoints are accessible:**
```bash
# Should return JSON with stats
curl http://localhost:5000/footlook/captures/stats
```

### "404 Not Found" on /footlook

Make sure you called:
```csharp
app.MapFootLookEndpoints();
```

### No data showing

1. Make some API calls to your endpoints
2. Check FootLook is not ignoring the paths:
   ```csharp
   options.IgnoredPaths.Add("/health"); // These won't be captured
   ```

---

## For Production Deployment

### Deploy Your API

Choose any platform:
- **Azure App Service**: `az webapp up --name my-api --runtime "DOTNETCORE:8.0"`
- **Railway**: Connect GitHub repo, auto-deploys
- **Render**: Connect GitHub repo, configure build commands

### Update Dashboard Connection

1. Deploy your API and get the public URL
2. Go to dashboard
3. Enter your production URL (e.g., `https://my-api.azurewebsites.net`)
4. Connect and monitor!

### Production CORS Configuration

For production, restrict CORS to specific origins:

```csharp
builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy =>
	{
		policy.WithOrigins(
			"https://footlook.pages.dev",
			"https://my-frontend.com"
		)
		.AllowAnyMethod()
		.AllowAnyHeader()
		.AllowCredentials();
	});
});
```

---

## Advanced Configuration

### Custom Sampling Rate

```csharp
options.SamplingRate = 0.1; // Capture 10% of requests
```

### Ignore Specific Paths

```csharp
options.IgnoredPaths.Add("/health");
options.IgnoredPaths.Add("/metrics");
options.IgnoredPaths.Add("/swagger");
```

### Filter by HTTP Methods

```csharp
options.AllowedMethods.Add("GET");
options.AllowedMethods.Add("POST");
// PUT, PATCH, DELETE won't be captured
```

### Limit Body Size

```csharp
options.MaxBodyLength = 1024 * 1024; // 1MB limit
```

### Use MongoDB for Persistence

```csharp
// Install: dotnet add package FootLook.Data
using FootLook.Data.Extensions;

builder.Services.AddFootLookMongoRepository();

// Configure connection string
builder.Configuration["MongoDb:ConnectionString"] = "mongodb://localhost:27017";
builder.Configuration["MongoDb:DatabaseName"] = "footlook";
```

---

## Next Steps

- ⭐ Star the repository
- 🐛 Report issues
- 💡 Suggest features
- 🤝 Contribute improvements

**Repository**: https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability

---

## Need Help?

- Check the [full documentation](../docs/ARCHITECTURE.md)
- Open an [issue on GitHub](https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability/issues)
- Review example implementations in `FootLook.Demo`

Happy monitoring! 🎉
