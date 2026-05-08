using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrokMcp.Services;

// Watches the build output directory and triggers a graceful host shutdown
// when grok-mcp.dll is rewritten. Combined with the launcher script, this
// makes `dotnet build -c Release` the only step needed for hot reload —
// the MCP exits cleanly, Claude Code reconnects, the wrapper re-syncs
// bin/ → runtime/ and the new code is live.
//
// Activation: set GROK_MCP_BUILD_DIR to the bin/Release/net10.0 path.
// The launcher scripts do this automatically.
public sealed class BuildWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BuildWatcher> _log;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _shutdownCts;

    public BuildWatcher(IHostApplicationLifetime lifetime, ILogger<BuildWatcher> log)
    {
        _lifetime = lifetime;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var buildDir = Environment.GetEnvironmentVariable("GROK_MCP_BUILD_DIR");
        if (string.IsNullOrWhiteSpace(buildDir) || !Directory.Exists(buildDir))
        {
            _log.LogDebug("BuildWatcher disabled (GROK_MCP_BUILD_DIR not set or missing)");
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(buildDir, "grok-mcp.dll")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnBuildChanged;
        _watcher.Created += OnBuildChanged;
        _watcher.Renamed += OnBuildChanged;

        _log.LogInformation("BuildWatcher armed on {BuildDir} — server will exit on rebuild", buildDir);
        return Task.CompletedTask;
    }

    private void OnBuildChanged(object sender, FileSystemEventArgs e)
    {
        _shutdownCts?.Cancel();
        _shutdownCts = new CancellationTokenSource();
        var cts = _shutdownCts;
        _ = Task.Delay(DebounceDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _log.LogInformation("Build artifact changed at {Path} — shutting down for hot reload", e.FullPath);
            _lifetime.StopApplication();
        }, TaskScheduler.Default);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _watcher = null;
        _shutdownCts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _shutdownCts?.Cancel();
    }
}
