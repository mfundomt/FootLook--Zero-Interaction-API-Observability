# FootLook Cloudflare Deployment Guide

## 🚀 Quick Start

This guide covers deploying FootLook with a **hybrid architecture**:
- **Backend (.NET API)**: Azure App Service / Railway / Render
- **Frontend (Dashboard)**: Cloudflare Pages

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                   Cloudflare Pages                      │
│             (Static HTML Dashboard - Free)              │
│                                                         │
│  • index.html (landing page)                           │
│  • dashboard.html (real-time dashboard)                │
└────────────────┬────────────────────────────────────────┘
				 │
				 │ WebSocket (SignalR)
				 │ REST API calls
				 │
┌────────────────▼────────────────────────────────────────┐
│              Backend (.NET 8 API)                       │
│     Azure / Railway / Render / Fly.io                   │
│                                                         │
│  • FootLook Middleware                                 │
│  • SignalR Hub (/footlook/live)                        │
│  • API Endpoints (/footlook/*)                         │
└────────────────┬────────────────────────────────────────┘
				 │
				 │ MongoDB Connection
				 │
┌────────────────▼────────────────────────────────────────┐
│                  MongoDB Atlas                          │
│                  (Free Tier)                            │
└─────────────────────────────────────────────────────────┘
```

---

## Step 1: Deploy Backend (.NET API)

### Option A: Azure App Service (Recommended for Portfolio)

1. **Install Azure CLI**:
   ```powershell
   # If not already installed
   winget install Microsoft.AzureCLI
   ```

2. **Login and deploy**:
   ```powershell
   az login

   # Create resource group
   az group create --name footlook-rg --location eastus

   # Create app service plan (Free tier for testing)
   az appservice plan create --name footlook-plan --resource-group footlook-rg --sku F1

   # Create web app
   az webapp create --name footlook-api-demo --resource-group footlook-rg --plan footlook-plan --runtime "DOTNET:8.0"

   # Deploy from your project folder
   cd C:\Users\mfund\source\repos\FootLook--Zero-Interaction-API-Observability\FootLook\FootLook.Demo
   az webapp up --name footlook-api-demo --resource-group footlook-rg
   ```

3. **Configure CORS** (required for Cloudflare Pages):
   ```powershell
   az webapp cors add --resource-group footlook-rg --name footlook-api-demo --allowed-origins "https://*.pages.dev" "https://*.cloudflare.com"
   ```

4. **Set MongoDB connection string**:
   ```powershell
   az webapp config appsettings set --resource-group footlook-rg --name footlook-api-demo --settings MongoDb__ConnectionString="your-mongodb-connection-string"
   ```

Your API will be available at: `https://footlook-api-demo.azurewebsites.net`

---

### Option B: Railway (Easiest Free Option)

1. Go to https://railway.app
2. Connect your GitHub repository
3. Select `FootLook.Demo` as the root directory
4. Railway auto-detects .NET 8 and deploys
5. Add environment variable: `MongoDb__ConnectionString`
6. Get your public URL (e.g., `https://footlook-production.up.railway.app`)

---

### Option C: Render

1. Go to https://render.com
2. New → Web Service
3. Connect GitHub repo
4. Build Command: `dotnet publish -c Release -o out`
5. Start Command: `cd out && dotnet FootLook.Demo.dll`
6. Add environment variable for MongoDB

---

## Step 2: Setup MongoDB (if not already configured)

1. Go to https://www.mongodb.com/cloud/atlas
2. Create free M0 cluster
3. Create database user
4. Whitelist IP: `0.0.0.0/0` (allow all - for testing)
5. Get connection string
6. Add to your backend environment variables

---

## Step 3: Deploy Frontend to Cloudflare Pages

### ✨ Dynamic Configuration Feature

The dashboard now includes a **built-in configuration UI** that allows anyone to connect their own FootLook API without editing code! This makes your portfolio demo instantly usable by other developers.

**Features:**
- 🔧 Dynamic backend URL configuration
- 💾 Auto-saves configuration in browser localStorage
- ✅ Connection testing before connecting
- 🚀 One-click localhost setup
- 📊 Real-time connection status indicator

**No code changes required** - just deploy as-is!

### Deploy to Cloudflare Pages

**Option 1: Via GitHub (Recommended)**

1. Create a new folder in your repo:
   ```
   FootLook/
   └── deploy/
	   └── cloudflare/
		   ├── index.html
		   └── dashboard.html
   ```

2. Push to GitHub:
   ```powershell
   git add deploy/cloudflare/
   git commit -m "Add Cloudflare Pages deployment files"
   git push
   ```

3. Go to https://dash.cloudflare.com
4. Pages → Create a project → Connect to Git
5. Select your repository
6. Build settings:
   - **Build command**: (leave empty)
   - **Build output directory**: `deploy/cloudflare`
   - **Root directory**: `deploy/cloudflare`
7. Deploy!

Your dashboard will be live at: `https://footlook.pages.dev`

**Option 2: Direct Upload**

1. Go to Cloudflare Pages
2. Upload assets directly
3. Upload `index.html` and `dashboard.html`

---

## Step 4: Configure CORS on Backend

Your backend needs to allow requests from Cloudflare Pages **and any developer's browser**.

### For Dynamic Configuration Support

Since developers will connect from different origins (their localhost, your Cloudflare Pages, etc.), you need a permissive CORS policy:

**Add to `Program.cs` (before `app.Run();`):**

```csharp
app.UseCors(policy => policy
	.SetIsOriginAllowed(origin => true) // Allow any origin for portfolio demo
	.AllowAnyMethod()
	.AllowAnyHeader()
	.AllowCredentials());
```

**For production environments**, use specific origins:

```csharp
app.UseCors(policy => policy
	.WithOrigins(
		"https://footlook.pages.dev",
		"https://*.pages.dev",
		"http://localhost:3000",
		"http://localhost:5000"
	)
	.AllowAnyMethod()
	.AllowAnyHeader()
	.AllowCredentials());
```

### Enable WebSockets (Required for SignalR)

**Azure App Service:**
```powershell
az webapp config set --resource-group footlook-rg --name footlook-api-demo --web-sockets-enabled true
```

**Railway/Render:** WebSockets are enabled by default.

---

## Testing the Deployment

1. **Deploy Your Backend API** to Azure/Railway/Render

2. **Open the Dashboard**:
   ```
   https://footlook.pages.dev
   ```

3. **Configure Backend Connection**:
   - Enter your backend API URL in the configuration panel
   - Click "Test Connection" to verify
   - Click "Connect" to start monitoring

4. **Generate Test Traffic**:
   - Use Swagger UI: `https://your-api.azurewebsites.net/swagger`
   - Make API calls and watch them appear in real-time

### For Other Developers Testing Your Project

Anyone can use your deployed dashboard to monitor their own API:

1. Deploy their own FootLook-enabled API
2. Visit your Cloudflare Pages URL
3. Enter their API URL in the config panel
4. Start monitoring immediately

**No code changes, no deployment, no configuration files** - just works!

---

## Portfolio Presentation

### Landing Page (index.html)

Your portfolio visitors will see:
- ✅ Professional landing page
- ✅ Project description
- ✅ Live demo button
- ✅ GitHub link

### Demo Strategy

**Option 1: Self-Generating Traffic**
Create a simple background job that generates sample API traffic:

```csharp
// Add to Program.cs
app.MapGet("/demo/generate-traffic", async () =>
{
	var random = new Random();
	var endpoints = new[] { "/api/users", "/api/products", "/api/orders" };
	var methods = new[] { "GET", "POST", "PUT", "DELETE" };

	// Generate 10 sample requests
	for (int i = 0; i < 10; i++)
	{
		// Your traffic generation logic
		await Task.Delay(random.Next(100, 500));
	}

	return Results.Ok("Traffic generated");
});
```

**Option 2: Demo Video**
- Record a screen capture showing the dashboard in action
- Embed on your portfolio/README

---

## Costs

| Service | Tier | Cost |
|---------|------|------|
| **Cloudflare Pages** | Free | $0/month |
| **MongoDB Atlas** | M0 Free Tier | $0/month |
| **Azure App Service** | F1 Free Tier | $0/month (limited hours) |
| **Azure App Service** | B1 Basic | ~$13/month |
| **Railway** | Free Tier | $0/month (500 hrs) |
| **Render** | Free Tier | $0/month |

**Recommended for Portfolio**: Railway Free + Cloudflare Pages = $0/month

---

## Custom Domain (Optional)

### Cloudflare Pages Custom Domain

1. Buy domain through Cloudflare or transfer existing
2. Pages → Custom domains → Add domain
3. `footlook.yourdomain.com`

### Backend Custom Domain

**Azure**:
```powershell
az webapp config hostname add --webapp-name footlook-api-demo --resource-group footlook-rg --hostname api.yourdomain.com
```

---

## Portfolio Tips

### Resume/LinkedIn Description

> **FootLook - Zero-Interaction API Observability Platform**
> 
> Built a production-grade .NET 8 middleware for real-time API monitoring with:
> - 🏗️ ASP.NET Core middleware with zero-config setup
> - ⚡ SignalR for real-time WebSocket streaming
> - 📊 Interactive dashboard with Chart.js visualizations
> - 🗄️ MongoDB integration for persistence
> - ☁️ Hybrid cloud deployment: Azure + Cloudflare Pages
> - 🎯 Advanced filtering, search, and analytics
> 
> [Live Demo](https://footlook.pages.dev) | [GitHub](https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability)

### GitHub README Enhancements

Add to your README:

```markdown
## 🌐 Live Demo

**Dashboard**: https://footlook.pages.dev  
**API**: https://footlook-api-demo.azurewebsites.net

Try it yourself! The dashboard monitors a live .NET 8 API with FootLook middleware installed.

## 🏗️ Architecture

- **Frontend**: Static HTML/JS on Cloudflare Pages (global CDN)
- **Backend**: .NET 8 API on Azure App Service
- **Real-time**: SignalR WebSocket streaming
- **Database**: MongoDB Atlas for capture storage
```

---

## Troubleshooting

### Dashboard shows "Disconnected"
- Check CORS is configured on backend
- Verify SignalR hub URL is correct
- Check browser console for errors

### SignalR connection fails
- Ensure WebSockets are enabled on Azure:
  ```powershell
  az webapp config set --resource-group footlook-rg --name footlook-api-demo --web-sockets-enabled true
  ```

### No data showing
- Generate test traffic using Swagger
- Check MongoDB connection string
- Verify FootLook is enabled in configuration

---

## Next Steps

1. ✅ Deploy backend to Railway/Azure
2. ✅ Configure MongoDB Atlas
3. ✅ Update `dashboard.html` with backend URL
4. ✅ Deploy to Cloudflare Pages
5. ✅ Test end-to-end
6. ✅ Add to portfolio/resume
7. ✅ Share on LinkedIn with demo link

---

## Support

- GitHub Issues: https://github.com/mfundomt/FootLook--Zero-Interaction-API-Observability/issues
- Cloudflare Pages Docs: https://developers.cloudflare.com/pages/
- Azure Docs: https://docs.microsoft.com/azure/app-service/
