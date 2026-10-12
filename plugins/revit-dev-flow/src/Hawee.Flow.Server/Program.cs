using System.Reflection;
using Hawee.Flow.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// hawee-flow: MCP server (stdio) cho luồng BIM_KB_Base.
//   hawee-flow                         -> chạy MCP server trên stdio
//   hawee-flow call <tool> [json]      -> gọi một tool từ dòng lệnh (kiểm tay, script, CI)
//   hawee-flow tools                   -> liệt kê tool
if (args.Length > 0 && args[0] is "call" or "tools")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    return Cli.Run(args);
}

var builder = Host.CreateApplicationBuilder(args);
// stdout là kênh MCP: mọi log đi stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "hawee-flow", Version = Cli.Version })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(Assembly.GetExecutingAssembly());
await builder.Build().RunAsync();
return 0;
