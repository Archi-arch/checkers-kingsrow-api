using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Checkers.Api.Services;

// Clean up any lingering worker processes prior to startup
ChildProcessTracker.KillOrphanedChinookProcesses();

var builder = WebApplication.CreateBuilder(args);

// Configure listening port (Render and cloud hosts provide PORT; default to 5114 or ASPNETCORE_URLS)
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}
else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) && !args.Any(a => a.StartsWith("--urls=")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5114");
}


// Register controllers
builder.Services.AddControllers();

// Configure Swagger / OpenAPI for both Development and Production environments
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Checkers Chinook & KingsRow REST Web API",
        Version = "v1",
        Description = "High-performance REST API for 8x8 English Draughts (American Checkers) integrating KingsRow/Chinook engine, opening books, and endgame tablebases."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

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

// Enable Swagger in Production and Development
bool enableSwagger = app.Configuration.GetValue<bool>("Swagger:Enabled", true);
if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Checkers API v1");
        c.RoutePrefix = "swagger";
        c.DocumentTitle = "Checkers Chinook & KingsRow API - Swagger";
    });
}

app.UseCors();
app.UseAuthorization();
app.MapControllers();

// Endpoint for graceful shutdown from the web UI (guarded in production cloud environments)
app.MapPost("/shutdown", (IHostApplicationLifetime lifetime, IWebHostEnvironment env) =>
{
    if (env.IsProduction() && Environment.GetEnvironmentVariable("ALLOW_REMOTE_SHUTDOWN") != "true")
    {
        return Results.Json(new { message = "Remote shutdown is disabled in production hosting environments." }, statusCode: 403);
    }
    _ = Task.Run(async () =>
    {
        await Task.Delay(200);
        lifetime.StopApplication();
    });
    return Results.Ok(new { message = "Server shutting down..." });
});

// Automatically launch browser when running locally on Windows (not under IIS or in Docker)
bool isUnderIis = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_POOL_ID"));
bool isContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
if (!isUnderIis && !isContainer && !isLinux && Environment.GetEnvironmentVariable("NO_BROWSER") == null)
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
    var effectivePort = Environment.GetEnvironmentVariable("PORT") ?? "5114";
    Console.WriteLine("==========================================================");
    Console.WriteLine("  Checkers Chinook & KingsRow API Server (.NET 8)");
    Console.WriteLine($"  Web UI:  http://0.0.0.0:{effectivePort}");
    Console.WriteLine($"  Swagger: http://0.0.0.0:{effectivePort}/swagger");
    Console.WriteLine("  Press Ctrl+C in this console to stop the server.");
    Console.WriteLine("==========================================================");
    app.Run();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n[ERROR] Server startup/runtime error: {ex.Message}");
    Console.ResetColor();
    if (!isContainer && !isLinux)
    {
        Console.WriteLine("\nPress any key to exit...");
        try { Console.ReadKey(); } catch { }
    }
}
finally
{
    ChildProcessTracker.KillOrphanedChinookProcesses();
}