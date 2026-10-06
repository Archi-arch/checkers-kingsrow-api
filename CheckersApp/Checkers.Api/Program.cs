using System.Diagnostics;
using Checkers.Api.Services;

// Clean up any lingering worker processes prior to startup
ChildProcessTracker.KillOrphanedChinookProcesses();

var builder = WebApplication.CreateBuilder(args);

// Configure default port 5114 unless overridden by arguments/environment
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) && !args.Any(a => a.StartsWith("--urls=")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5114");
}

// Register controllers
builder.Services.AddControllers();

// Register core services
builder.Services.AddSingleton<IPdnService, PdnService>();
builder.Services.AddSingleton<ILruCacheService, LruCacheService>();

// Register ChinookEnginePool as IHostedService (for warm background startup)
// and as IEnginePool (for DI injection into controllers)
builder.Services.AddSingleton<ChinookEnginePool>();
builder.Services.AddSingleton<IEnginePool>(sp => sp.GetRequiredService<ChinookEnginePool>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChinookEnginePool>());

// Allow CORS for easy web board integration
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();
app.UseAuthorization();
app.MapControllers();

// Endpoint for graceful shutdown from the web UI
app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(200);
        lifetime.StopApplication();
    });
    return Results.Ok(new { message = "Server shutting down..." });
});

// Automatically launch browser when running locally (not under IIS)
bool isUnderIis = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_POOL_ID"));
if (!isUnderIis && Environment.GetEnvironmentVariable("NO_BROWSER") == null)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(1200);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://127.0.0.1:5114",
                UseShellExecute = true
            });
        }
        catch { }
    });
}

try
{
    Console.WriteLine("==========================================================");
    Console.WriteLine("  Checkers Chinook & KingsRow API Server (.NET 8)");
    Console.WriteLine("  Web UI: http://127.0.0.1:5114");
    Console.WriteLine("  Press Ctrl+C in this console to stop the server.");
    Console.WriteLine("==========================================================");
    app.Run();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n[ERROR] Server startup/runtime error: {ex.Message}");
    Console.ResetColor();
    Console.WriteLine("\nPress any key to exit...");
    try { Console.ReadKey(); } catch { }
}
finally
{
    ChildProcessTracker.KillOrphanedChinookProcesses();
}