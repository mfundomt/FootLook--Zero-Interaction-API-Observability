using FootLook.Core.Services;
using FootLook.Core.Extensions;
using FootLook.Core.Options;
using FootLook.Core.Hubs;
using FootLook.Data.Repositories;
using FootLook.Data.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//Register SignalR for real-time updates
builder.Services.AddSignalR();


builder.Services.AddFootLook(options =>
{
    builder.Configuration.GetSection("FootLook").Bind(options);

    options.CaptureRequestBody = true;
    options.CaptureResponseBody = true;
    options.MaxBodyLength = 1024 * 1024;
    options.EndpointBasePath = "/footlook";
    options.QueCapacity = 10_000;
    options.Enabled = true;
    options.SamplingRate = 1.0;

    options.ServiceName = "FootLook.Demo";
    options.EnvironmentName = builder.Environment.EnvironmentName;

    options.IgnoredPaths.Add("/footlook");
    options.IgnoredPaths.Add("/footlook.html");
    options.IgnoredPaths.Add("/swagger");
    options.IgnoredPaths.Add("/favicon.ico");
    options.IgnoredPaths.Add("/.well-known");

    options.AllowedMethods.Add("GET");
    options.AllowedMethods.Add("POST");
    options.AllowedMethods.Add("PATCH");
    options.AllowedMethods.Add("PUT");
    options.AllowedMethods.Add("DELETE");
});

builder.Services.AddFootLookMongoRepository();

//register the dependency
//builder.Services.AddSingleton<IShadowSink, InMemorySink>();
//builder.Services.AddSingleton<IShadowQueue, ShadowQueue>();
//builder.Services.AddHostedService<ShadowBackgroundWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
#region FootLook Middleware Flow (Manual for testing)
//var sink = new InMemorySink();

//await sink.WriteAsync(new CapturedRequest
//{
//    //Id = Guid.NewGuid(),
//    Method = "GET",
//    Path = "/api/values",
//    StatusCode = 200,
//    //Headers = new Dictionary<string, string>()
//});

//var captures = sink.GetAll();

//Console.WriteLine($"Captured {captures.Count} requests.");
#endregion

//app.UseMiddleware<ShadowMiddleware>();

app.UseFootLook();


var footlookOptions = app.Services.GetRequiredService<FootLookOptions>();
app.MapFootLookEndpoints(footlookOptions);

app.MapGet("/", () =>
{
    return "FootLook Running";
});

app.MapPost("/test2", (TestRequest request) =>
{
    return Results.Ok(new
    {
        Message = "Controller received body",
        Body = request
    });
});

app.MapGet("/slow", async () =>
{
   await Task.Delay(2000);
    return "This is a slow endpoint";
});

app.MapGet("/error", () =>
{
   throw new Exception("simulated failure");
});

app.MapHub<CaptureHub>("/footlook/live");

app.MapGet("/mongo-test",
    async (ICaptureRepository repository) =>
    {
        var captures =
            await repository.GetRecentAsync(10);

        return Results.Ok(captures);
    });


var events = app.Services.GetRequiredService<CaptureEvents>();

events.OnRequestCaptured += capture =>
{
    Console.WriteLine(
        $"[LIVE] {capture.Method} {capture.Path} " +
        $"{capture.StatusCode} " +
        $"{capture.DurationMs}ms");
};

app.Run();


public class TestRequest
{
    public string Message { get; set; } = string.Empty;
}