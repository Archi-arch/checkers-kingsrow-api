using System.Diagnostics;
using Checkers.Api.Models;

namespace Checkers.Api.Services;

public interface IEnginePool
{
    Task<SuggestResponse> SuggestMoveAsync(string pdn, int softTimeMs, int depth, CancellationToken ct);
    int ActiveWorkersCount { get; }
    bool IsHealthy { get; }
}

public class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;

    public bool HasExited => _process.HasExited;

    public WorkerProcess(string exePath)
    {
        string workDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        ProcessStartInfo psi;

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            string wineCmd = File.Exists("/usr/local/bin/wine64") ? "/usr/local/bin/wine64"
                : (File.Exists("/usr/lib/wine/wine64") ? "/usr/lib/wine/wine64"
                : (File.Exists("/usr/bin/wine64") ? "/usr/bin/wine64"
                : (File.Exists("/usr/bin/wine") ? "/usr/bin/wine" : "wine")));
            psi = new ProcessStartInfo
            {
                FileName = wineCmd,
                Arguments = $"\"{exePath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["WINEDEBUG"] = "-all";
            psi.EnvironmentVariables["WINEARCH"] = "win64";
            psi.EnvironmentVariables["DISPLAY"] = "";
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {exePath}");
        ChildProcessTracker.TrackProcess(_process);
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (_stderrLog) _stderrLog.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
    }

    private readonly System.Text.StringBuilder _stderrLog = new();

    public async Task WarmUpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _stdout.ReadLineAsync(ct);
            if (line == null)
            {
                string errText;
                lock (_stderrLog) errText = _stderrLog.ToString();
                throw new InvalidOperationException($"Worker process terminated unexpectedly during warmup. Stderr:\n{errText}");
            }
            if (line.Contains("READY", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    public async Task WriteLineAsync(string text, CancellationToken ct)
    {
        await _stdin.WriteLineAsync(text.AsMemory(), ct);
        await _stdin.FlushAsync(ct);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        return await _stdout.ReadLineAsync(ct);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _stdin.WriteLine("QUIT");
                if (!_process.WaitForExit(1000))
                {
                    _process.Kill();
                }
            }
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}

public class WorkerSlot : IDisposable
{
    private readonly string _exePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WorkerProcess? _process;
    private ChinookCliAdapter? _adapter;

    public WorkerSlot(string exePath, ILogger logger)
    {
        _exePath = exePath;
        _logger = logger;
    }

    public SemaphoreSlim Lock => _lock;
    public ChinookCliAdapter Adapter => _adapter ?? throw new InvalidOperationException("Worker is not initialized.");
    public bool IsAlive => _process != null && !_process.HasExited;

    public async Task InitializeAsync(CancellationToken ct)
    {
        _process = new WorkerProcess(_exePath);
        await _process.WarmUpAsync(ct);
        _adapter = new ChinookCliAdapter(_process);
    }

    public void Restart()
    {
        try
        {
            _logger.LogWarning("Restarting worker process at {Path}...", _exePath);
            _process?.Dispose();
        }
        catch
        {
        }

        try
        {
            _process = new WorkerProcess(_exePath);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            _process.WarmUpAsync(cts.Token).GetAwaiter().GetResult();
            _adapter = new ChinookCliAdapter(_process);
            _logger.LogInformation("Worker process restarted successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart worker process.");
        }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _lock.Dispose();
    }
}

public class ChinookEnginePool : IHostedService, IEnginePool
{
    private readonly IConfiguration _config;
    private readonly ILogger<ChinookEnginePool> _logger;
    private WorkerSlot[] _workers = Array.Empty<WorkerSlot>();
    private int _roundRobinIndex = 0;

    public ChinookEnginePool(IConfiguration config, ILogger<ChinookEnginePool> logger)
    {
        _config = config;
        _logger = logger;
    }

    public int ActiveWorkersCount => _workers.Count(w => w.IsAlive);
    public bool IsHealthy => _workers.Any(w => w.IsAlive);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "Workers", "chinook.exe");

        string? configuredPath = _config["Engine:Path"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string candidate = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));

            if (File.Exists(candidate))
            {
                exePath = candidate;
            }
        }

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"Chinook worker executable not found at '{exePath}'. Make sure ChinookWorker was built.");
        }

        // Clean up any dangling chinook processes from previous abruptly terminated sessions
        ChildProcessTracker.KillOrphanedChinookProcesses();

        int workerCount = _config.GetValue<int>("Engine:Workers", 2);
        _logger.LogInformation("Starting {WorkerCount} long-lived Chinook worker(s) from {Path}...", workerCount, exePath);

        var slotList = new List<WorkerSlot>();
        for (int i = 0; i < workerCount; i++)
        {
            var slot = new WorkerSlot(exePath, _logger);
            await slot.InitializeAsync(cancellationToken);
            slotList.Add(slot);
        }

        _workers = slotList.ToArray();
        _logger.LogInformation("All {WorkerCount} Chinook worker(s) warmed up and ready.", workerCount);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var s in _workers) s.Dispose();
            ChildProcessTracker.KillOrphanedChinookProcesses();
        };
    }

    public async Task<SuggestResponse> SuggestMoveAsync(string pdn, int softTimeMs, int depth, CancellationToken ct)
    {
        if (_workers.Length == 0)
        {
            throw new InvalidOperationException("No engine workers available in the pool.");
        }

        // Round-robin worker selection with async lock per worker slot
        int index = (int)((uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)_workers.Length);
        var slot = _workers[index];

        await slot.Lock.WaitAsync(ct);
        try
        {
            if (!slot.IsAlive)
            {
                slot.Restart();
            }

            await slot.Adapter.SetPositionAsync(pdn, ct);
            var result = await slot.Adapter.SearchAsync(softTimeMs, depth, ct);

            return new SuggestResponse(
                Engine: result.Engine,
                BestMove: result.BestMove,
                Pv: result.Pv,
                ScoreOrWDL: result.ScoreOrWDL,
                Depth: result.Depth,
                Nodes: result.Nodes,
                PositionKey: result.PositionKey,
                Info: new SuggestResponseInfo(
                    TablebaseHit: result.TablebaseHit,
                    TimeMs: result.TimeMs
                )
            );
        }
        catch (OperationCanceledException)
        {
            // If search was cancelled or timed out, restart worker process to prevent stdin/stdout desynchronization
            slot.Restart();
            throw;
        }
        catch (Exception)
        {
            slot.Restart();
            throw;
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Chinook worker pool...");
        foreach (var slot in _workers)
        {
            slot.Dispose();
        }
        ChildProcessTracker.KillOrphanedChinookProcesses();

        return Task.CompletedTask;
    }
}