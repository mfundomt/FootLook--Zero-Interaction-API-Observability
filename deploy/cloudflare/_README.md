# FootLook - Cloudflare Pages Deployment Files

This folder contains everything needed to deploy FootLook dashboard to **Cloudflare Pages** with dynamic backend configuration.

## 📁 Files

- **`index.html`** - Landing page with project information
- **`dashboard.html`** - Real-time monitoring dashboard with dynamic backend configuration
- **`DEPLOYMENT.md`** - Complete deployment guide (backend + frontend)
- **`QUICKSTART.md`** - 5-minute guide for developers to try FootLook
- **`README.md`** - This file (portfolio/documentation overview)

## ✨ Key Feature: Dynamic Backend Configuration

The dashboard includes a **built-in configuration UI** that allows anyone to connect their own FootLook-enabled API without editing code!

### How It Works

1. Developer visits your deployed dashboard
2. Enters their backend API URL in the config panel
3. Clicks "Test Connection" to verify
4. Clicks "Connect" to start monitoring
5. Configuration is saved in browser localStorage

### Why This Matters for Your Portfolio

- ✅ **Interactive Demo**: Recruiters can test with their own APIs
- ✅ **Zero Friction**: No code editing, no redeployment needed
- ✅ **Professional**: Shows you understand real-world usability
- ✅ **Shareable**: Other developers can actually use your tool

## 🚀 Quick Deploy

### 1. Deploy Backend (Choose One)

**Azure:**
```bash
cd ../../../FootLook/FootLook.Demo
az webapp up --name footlook-api --runtime "DOTNETCORE:8.0"
```

**Railway:**
- Connect GitHub repo
- Select `FootLook.Demo` project
- Auto-deploys

### 2. Deploy Frontend to Cloudflare Pages

**Via GitHub (Recommended):**
1. Push this folder to your repository
2. Go to https://dash.cloudflare.com → Pages
3. Create new project → Connect Git
4. Build settings:
   - Build output directory: `deploy/cloudflare`
   - Root directory: `deploy/cloudflare`
5. Deploy!

**Direct Upload:**
1. Upload `index.html` and `dashboard.html`
2. Done!

### 3. Configure CORS

Add to your backend `Program.cs`:

```csharp
app.UseCors(policy => policy
	.SetIsOriginAllowed(origin => true)
	.AllowAnyMethod()
	.AllowAnyHeader()
	.AllowCredentials());
```

## 📊 What Visitors Will See

### Landing Page (`index.html`)
- Project overview
- Key features
- Links to dashboard and GitHub

### Dashboard (`dashboard.html`)
- Backend configuration panel
- Connection testing
- Real-time API monitoring:
  - Request/response capture
  - Performance metrics
  - Error tracking
  - Filtering & search
  - Data export

## 🎯 Portfolio Presentation

### For Your Resume/LinkedIn

> **FootLook - Zero-Interaction API Observability**
> 
> Real-time API monitoring platform with .NET 8 middleware and SignalR WebSocket streaming.
> 
> - 🏗️ Hybrid cloud deployment (Azure + Cloudflare Pages)
> - ⚡ Sub-50ms WebSocket latency for live updates
> - 🎯 Dynamic configuration UI for zero-friction onboarding
> - 📊 Chart.js visualizations with RPM trending
> - 🗄️ MongoDB persistence layer
> 
> **Live Demo**: https://footlook.pages.dev  
> **GitHub**: https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability

### Demo Strategy

**Option 1: Pre-configured Demo API**
- Deploy FootLook.Demo with sample data generation
- Dashboard auto-connects to your demo API
- Visitors see immediate activity

**Option 2: DIY Connection**
- Visitors enter their own API URL
- Shows tool flexibility
- More interactive experience

**Option 3: Hybrid (Recommended)**
- Default to your demo API
- Provide "Use Your Own API" button
- Best of both worlds

## 📚 Documentation Links

- **Full Deployment Guide**: [DEPLOYMENT.md](./DEPLOYMENT.md)
- **Developer Quick Start**: [QUICKSTART.md](./QUICKSTART.md)
- **Architecture Overview**: [../../docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md)
- **Main Project README**: [../../README.md](../../README.md)

## 🔧 Local Testing

### Test the Dashboard Locally

```bash
# Serve files with any static server
npx serve .

# Or use Python
python -m http.server 8000

# Or use .NET
dotnet tool install --global dotnet-serve
dotnet serve
```

Then open: `http://localhost:8000/dashboard.html`

### Connect to Local Backend

1. Run your FootLook-enabled API:
   ```bash
   cd ../../FootLook/FootLook.Demo
   dotnet run
   ```

2. In the dashboard, enter:
   ```
   http://localhost:5000
   ```

3. Click "Test Connection" then "Connect"

## 🌐 Live URLs

After deployment:

- **Landing Page**: `https://footlook.pages.dev`
- **Dashboard**: `https://footlook.pages.dev/dashboard.html`
- **Backend API**: `https://your-api.azurewebsites.net`

## 💡 Tips for Recruiters/Visitors

### If You Want to Try FootLook:

1. **Quick Test** (No Setup):
   - Click "Launch Dashboard"
   - See the pre-configured demo

2. **Test with Your API** (5 minutes):
   - Follow [QUICKSTART.md](./QUICKSTART.md)
   - Add FootLook to your .NET API
   - Connect via the dashboard UI

3. **Explore the Code**:
   - Check out the [GitHub repository](https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability)
   - Read the [architecture docs](../../docs/ARCHITECTURE.md)

## 🐛 Troubleshooting

### Dashboard shows "Connection failed"

1. Check CORS is configured on backend
2. Verify WebSockets are enabled
3. Test endpoint manually:
   ```bash
   curl https://your-api.com/footlook/captures/stats
   ```

### "No backend configured" message

- Enter your API URL in the config panel
- Make sure the URL is valid (http:// or https://)
- Click "Test Connection" first

### SignalR connection drops

- Check backend is still running
- Verify WebSockets aren't blocked by firewall
- Try reconnecting from the dashboard

## 📈 Analytics & Metrics

Track your demo's impact:

- Cloudflare Analytics (free with Pages)
- GitHub traffic insights
- LinkedIn post analytics

## 🤝 Contributing

Found an issue or have an improvement?

1. Open an issue: https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability/issues
2. Submit a PR
3. Contact via LinkedIn

## 📄 License

MIT License - See main repository LICENSE file

---

<p align="center">
  <strong>Built with ❤️ using .NET 8, SignalR, and Cloudflare Pages</strong>
</p>
