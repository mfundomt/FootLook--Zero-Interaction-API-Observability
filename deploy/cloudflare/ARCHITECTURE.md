# FootLook Architecture & Deployment Flow

## 🏗️ System Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         DEVELOPER'S BROWSER                         │
│                                                                     │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │             FootLook Dashboard (dashboard.html)               │ │
│  │                                                               │ │
│  │  ┌─────────────────────────────────────────────────────────┐ │ │
│  │  │  🔧 Backend Configuration UI                            │ │ │
│  │  │  Input: https://api.example.com                         │ │ │
│  │  │  [Test Connection] [Connect] [Use Localhost]            │ │ │
│  │  │  Status: 🟢 Connected                                   │ │ │
│  │  └─────────────────────────────────────────────────────────┘ │ │
│  │                                                               │ │
│  │  📊 Real-time Dashboard                                       │ │
│  │  • Request/Response Capture                                   │ │
│  │  • Performance Metrics                                        │ │
│  │  • Filtering & Search                                         │ │
│  │  • RPM Charts                                                 │ │
│  └───────────────────────────────────────────────────────────────┘ │
│                           │                                         │
│                           │ localStorage (saves URL)                │
│                           │                                         │
└───────────────────────────┼─────────────────────────────────────────┘
							│
							│ HTTPS / WebSocket (SignalR)
							│ • REST API calls (fetch)
							│ • SignalR streaming
							│ • CORS: SetIsOriginAllowed(true)
							│
┌───────────────────────────▼─────────────────────────────────────────┐
│                    CLOUDFLARE PAGES (Frontend)                      │
│                    https://footlook.pages.dev                       │
│                                                                     │
│  Static Files (Global CDN):                                         │
│  • index.html (landing page)                                        │
│  • dashboard.html (monitoring UI)                                   │
│  • _redirects (routing rules)                                       │
│                                                                     │
│  ✅ Free                                                            │
│  ✅ Global CDN                                                      │
│  ✅ Auto HTTPS                                                      │
│  ✅ Instant deploys                                                 │
└─────────────────────────────────────────────────────────────────────┘


							│
							│ API Requests
							│ • /footlook/captures/stats (REST)
							│ • /footlook/captures/history (REST)
							│ • /footlook/live (SignalR Hub)
							│
┌───────────────────────────▼─────────────────────────────────────────┐
│               AZURE / RAILWAY / RENDER (Backend)                    │
│           https://footlook-api.azurewebsites.net                    │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │                    ASP.NET Core Pipeline                     │   │
│  │                                                              │   │
│  │  HTTP Request → CORS Middleware                              │   │
│  │       ↓                                                      │   │
│  │  FootLook Middleware (ShadowMiddleware.cs)                   │   │
│  │       ↓                                                      │   │
│  │  Capture Request/Response                                    │   │
│  │       ↓                                                      │   │
│  │  Enqueue to ShadowQueue                                      │   │
│  │       ↓                                                      │   │
│  │  Continue to Your Controllers                                │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                           │                                         │
│  ┌────────────────────────▼────────────────────────────────────┐   │
│  │         Background Worker (ShadowBackgroundWorker)          │   │
│  │                                                              │   │
│  │  • Dequeues captures from queue                              │   │
│  │  • Processes in background                                   │   │
│  │  • Stores to repository                                      │   │
│  │  • Broadcasts to SignalR hub                                 │   │
│  └────────────────────────┬────────────────────────────────────┘   │
│                           │                                         │
│  ┌────────────────────────▼────────────────────────────────────┐   │
│  │            SignalR Hub (CaptureHub.cs)                      │   │
│  │                                                              │   │
│  │  • Real-time WebSocket streaming                             │   │
│  │  • Broadcasts "captureReceived" events                       │   │
│  │  • Connected clients receive live updates                    │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                           │                                         │
│  ┌────────────────────────▼────────────────────────────────────┐   │
│  │          REST Endpoints (FootLookEndpointExtensions)        │   │
│  │                                                              │   │
│  │  • GET /footlook/captures/stats                              │   │
│  │  • GET /footlook/captures/history?count=50                   │   │
│  │  • DELETE /footlook/captures                                 │   │
│  │  • Hub: /footlook/live                                       │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  💰 Cost: ~$13/mo (Azure B1) or $0 (Railway Free)                  │
│  ⚡ WebSockets: Enabled                                             │
│  🌐 CORS: Configured for all origins                                │
└───────────────────────────┬─────────────────────────────────────────┘
							│
							│ MongoDB Driver
							│ • Async operations
							│ • Connection pooling
							│
┌───────────────────────────▼─────────────────────────────────────────┐
│                    MONGODB ATLAS (Database)                         │
│                   mongodb+srv://cluster.mongodb.net                 │
│                                                                     │
│  Collections:                                                       │
│  • captures (request/response data)                                 │
│  • stats (aggregated metrics)                                       │
│                                                                     │
│  ✅ Free M0 Cluster (512MB)                                         │
│  ✅ Auto-scaling                                                    │
│  ✅ Backups included                                                │
└─────────────────────────────────────────────────────────────────────┘
```

## 🔄 Request Flow

### 1. Initial Connection

```
Developer → Cloudflare Pages → Opens dashboard.html
		 ↓
	Sees Config UI
		 ↓
	Enters API URL: https://footlook-api.azurewebsites.net
		 ↓
	Clicks "Test Connection"
		 ↓
	fetch(${BACKEND_URL}/footlook/captures/stats)
		 ↓
	✅ Status 200: API is reachable
		 ↓
	Clicks "Connect"
		 ↓
	SignalR connects to ${BACKEND_URL}/footlook/live
		 ↓
	🟢 Status: Connected
		 ↓
	localStorage saves URL for next time
```

### 2. Real-time Monitoring

```
User's API receives request → FootLook Middleware intercepts
						   ↓
					Captures req/res
						   ↓
					Enqueues to queue
						   ↓
			  Background Worker processes
						   ↓
			  Stores to MongoDB (optional)
						   ↓
			  Broadcasts via SignalR Hub
						   ↓
			  WebSocket → Dashboard
						   ↓
			  Updates UI in <50ms
```

### 3. Historical Data

```
Dashboard loads → fetch(${BACKEND_URL}/footlook/captures/history?count=50)
			   ↓
		Repository queries MongoDB
			   ↓
		Returns last 50 captures
			   ↓
		Dashboard displays in feed
```

## 🚀 Deployment Flow

### Frontend (Cloudflare Pages)

```
Developer → Push to GitHub
		 ↓
	GitHub webhook triggers Cloudflare
		 ↓
	Cloudflare Pages builds
		 ↓
	Deploys to global CDN
		 ↓
	Available at: https://footlook.pages.dev
		 ↓
	Auto HTTPS + CDN caching
```

### Backend (Azure App Service)

```
Developer → dotnet publish -c Release
		 ↓
	az webapp up --name footlook-api
		 ↓
	Uploads binaries to Azure
		 ↓
	App Service starts application
		 ↓
	Available at: https://footlook-api.azurewebsites.net
		 ↓
	Configure CORS + WebSockets
```

## 📦 Component Responsibilities

### Frontend (dashboard.html)

- ✅ Configuration UI (enter backend URL)
- ✅ Connection testing
- ✅ SignalR client connection
- ✅ Real-time data visualization
- ✅ Filtering, search, export
- ✅ localStorage persistence

### Backend (FootLook.Core)

- ✅ HTTP middleware interception
- ✅ Request/response capture
- ✅ Async queue processing
- ✅ SignalR hub broadcasting
- ✅ REST API endpoints
- ✅ MongoDB persistence

### Database (MongoDB)

- ✅ Capture storage
- ✅ Statistics aggregation
- ✅ Historical queries
- ✅ Optional (works without it)

## 🔐 Security Considerations

### CORS Configuration

```csharp
// Development: Allow all (for portfolio demo)
app.UseCors(policy => policy
	.SetIsOriginAllowed(origin => true)
	.AllowAnyMethod()
	.AllowAnyHeader()
	.AllowCredentials());

// Production: Restrict origins
app.UseCors(policy => policy
	.WithOrigins("https://footlook.pages.dev")
	.AllowAnyMethod()
	.AllowAnyHeader()
	.AllowCredentials());
```

### WebSocket Security

- ✅ SignalR uses secure WebSocket (wss://) over HTTPS
- ✅ Automatic reconnection with exponential backoff
- ✅ Connection lifecycle management

### Data Privacy

- ⚠️ Request/response bodies captured (sensitive data!)
- ✅ Configurable sampling rate
- ✅ Ignored paths (e.g., /health, /swagger)
- ✅ Max body size limits

## 🎯 Why This Architecture?

### Hybrid Approach Benefits

1. **Frontend on Cloudflare Pages**
   - Free hosting
   - Global CDN (fast everywhere)
   - No server maintenance
   - Instant cache invalidation

2. **Backend on Azure/Railway**
   - WebSocket support (required for SignalR)
   - .NET runtime environment
   - Vertical scaling options
   - MongoDB connectivity

3. **Dynamic Configuration**
   - No hardcoded URLs
   - Works for any developer
   - Portfolio-friendly
   - Easy demos

### Alternative Architectures

**All Azure:**
```
Azure Static Web Apps + Azure App Service + Azure Cosmos DB
Pros: Single platform, integrated
Cons: Higher cost, vendor lock-in
```

**All Serverless:**
```
Cloudflare Pages + Azure Functions + MongoDB Atlas
Pros: Pay per use, auto-scale
Cons: SignalR complexity, cold starts
```

**Containerized:**
```
Docker → Azure Container Apps / AWS ECS
Pros: Portable, scalable
Cons: More complex, higher cost
```

## 📊 Performance Characteristics

| Metric | Value |
|--------|-------|
| Middleware Overhead | <5ms per request |
| SignalR Latency | <50ms |
| Dashboard Load Time | <1s (CDN) |
| Max Throughput | 10,000 req/min (B1 tier) |
| Memory Usage | ~100MB baseline |
| WebSocket Concurrency | 1,000+ connections |

## 🔧 Configuration Options

### Minimal (In-Memory)

```csharp
builder.Services.AddFootLook(options =>
{
	options.Enabled = true;
});
```

### Full-Featured (MongoDB)

```csharp
builder.Services.AddFootLook(options =>
{
	options.CaptureRequestBody = true;
	options.CaptureResponseBody = true;
	options.MaxBodyLength = 1024 * 1024;
	options.SamplingRate = 1.0;
	options.ServiceName = "MyAPI";
});

builder.Services.AddFootLookMongoRepository();
```

---

## 📚 Related Documents

- [DEPLOYMENT.md](./DEPLOYMENT.md) - Step-by-step deployment
- [QUICKSTART.md](./QUICKSTART.md) - 5-minute setup
- [SUMMARY.md](./SUMMARY.md) - Portfolio checklist
- [README.md](./README.md) - Project overview
