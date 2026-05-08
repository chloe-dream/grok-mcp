using GrokMcp.Config;
using GrokMcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NReco.Logging.File;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Bind options from environment FIRST so logging can use them.
    builder.Services.Configure<GrokOptions>(GrokOptions.BindFromEnvironment);

    // ── Logging — CRITICAL: stdout is reserved for JSON-RPC. Force everything to stderr + a file.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var bootOpts = new GrokOptions();
    GrokOptions.BindFromEnvironment(bootOpts);
    if (Enum.TryParse<LogLevel>(bootOpts.LogLevel, ignoreCase: true, out var lvl))
        builder.Logging.SetMinimumLevel(lvl);

    if (!string.IsNullOrWhiteSpace(bootOpts.LogDir))
    {
        Directory.CreateDirectory(bootOpts.LogDir);
        var logFilePath = Path.Combine(bootOpts.LogDir, "grok-mcp-{0:yyyy-MM-dd}.log");
        builder.Logging.AddFile(logFilePath, fileOpts =>
        {
            fileOpts.Append = true;
            fileOpts.FormatLogFileName = name => string.Format(name, DateTime.Now);
        });
    }

    // Fail fast if API key is missing — surfaces in stderr/file before MCP handshake.
    if (string.IsNullOrWhiteSpace(bootOpts.ApiKey))
    {
        await Console.Error.WriteLineAsync(
            "FATAL: XAI_API_KEY environment variable is missing. Set it in .mcp.json env block or shell.");
        return 2;
    }

    // ── Services
    builder.Services.AddSingleton<ChatSessionStore>();
    builder.Services.AddSingleton<ImageInputResolver>();
    builder.Services.AddSingleton<ImageWriter>();
    builder.Services.AddHostedService<BuildWatcher>();
    builder.Services.AddHttpClient<GrokClient>((sp, http) =>
    {
        var opts = sp.GetRequiredService<IOptions<GrokOptions>>().Value;
        http.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
    });

    // ── MCP server (stdio transport, tools auto-discovered from this assembly)
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // Catch-all — never let an exception escape to stdout (would corrupt JSON-RPC stream).
    await Console.Error.WriteLineAsync($"FATAL: {ex.GetType().Name}: {ex.Message}");
    await Console.Error.WriteLineAsync(ex.StackTrace);
    return 1;
}
