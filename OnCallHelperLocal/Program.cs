using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnCallHelperLocal;
using OnCallHelperLocal.Data;
using OnCallHelperLocal.Embeddings;
using OnCallHelperLocal.Services;

var settings = LocalSettings.FromEnvironment();

// `setup` is a one-off command (it may print to stdout); everything else is the MCP server.
if (args.Length > 0 && args[0] == "setup")
    return await Setup.RunAsync(settings, wait: args.Contains("--wait"));

if (args.Length > 0 && args[0] == "import")
    return await Import.RunAsync(settings, args);
if (args.Length > 0 && args[0] == "reembed")
    return await Reembed.RunAsync(settings, args);

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(settings);
builder.Services.AddHttpClient<IEmbedder, OllamaEmbedder>(http =>
{
    http.BaseAddress = new Uri(settings.EmbeddingsBaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddSingleton<IncidentStore>();
builder.Services.AddSingleton<IncidentService>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "oncall-helper-local", Version = "0.2.0" };
        o.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
