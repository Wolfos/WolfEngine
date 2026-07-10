using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WolfEngine.Editor.Automation;

public static class Program
{
	public static async Task Main()
	{
		// The editor writes diagnostics through Console.Out; reserve stdout exclusively for MCP JSON-RPC.
		Console.SetOut(Console.Error);
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
		builder.Services.AddSingleton<EditorProcessController>();
		builder.Services
			.AddMcpServer(options => options.ServerInfo = new() { Name = "WolfEngine.Editor.Automation", Version = "1.0" })
			.WithStdioServerTransport()
			.WithTools<EditorAutomationTools>();

		using var host = builder.Build();
		await host.StartAsync().ConfigureAwait(false);
		try
		{
			host.Services.GetRequiredService<EditorProcessController>()
				.RunOnCurrentThread(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
		}
		finally
		{
			await host.StopAsync().ConfigureAwait(false);
		}
	}
}
