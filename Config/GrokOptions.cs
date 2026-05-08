using System;

namespace GrokMcp.Config;

public class GrokOptions
{
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://api.x.ai/v1";
    public string ChatModel { get; set; } = "grok-3-mini";
    public string CreativeModel { get; set; } = "grok-4-latest";
    public string ImageModel { get; set; } = "grok-imagine-image";
    public int HttpTimeoutSeconds { get; set; } = 300;
    public int SessionTurnCap { get; set; } = 50;
    public string LogDir { get; set; } = "";
    public string LogLevel { get; set; } = "Information";

    public static void BindFromEnvironment(GrokOptions o)
    {
        o.ApiKey = Environment.GetEnvironmentVariable("XAI_API_KEY") ?? "";
        o.ChatModel = NonEmpty("GROK_MCP_CHAT_MODEL", o.ChatModel);
        o.CreativeModel = NonEmpty("GROK_MCP_CREATIVE_MODEL", o.CreativeModel);
        o.ImageModel = NonEmpty("GROK_MCP_IMAGE_MODEL", o.ImageModel);
        o.LogLevel = NonEmpty("GROK_MCP_LOG_LEVEL", o.LogLevel);

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultLogDir = string.IsNullOrEmpty(localApp)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "logs")
            : System.IO.Path.Combine(localApp, "grok-mcp", "logs");
        o.LogDir = NonEmpty("GROK_MCP_LOG_DIR", defaultLogDir);

        if (int.TryParse(Environment.GetEnvironmentVariable("GROK_MCP_HTTP_TIMEOUT_SEC"), out var t) && t > 0)
            o.HttpTimeoutSeconds = t;
        if (int.TryParse(Environment.GetEnvironmentVariable("GROK_MCP_SESSION_CAP"), out var c) && c > 0)
            o.SessionTurnCap = c;
    }

    private static string NonEmpty(string envVar, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }
}
