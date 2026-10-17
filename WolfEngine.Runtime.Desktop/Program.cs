using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.UI;
using WolfEngine.Utility;

namespace WolfEngine.Runtime.Desktop;

/// <summary>
/// Runs a built game on macOS and Windows. Gameplay steps on its own thread while the render pipeline owns the main
/// thread, which it needs for the window and its event loop.
/// </summary>
public static class Program
{
	public static int Main(string[] args)
	{
		try
		{
			return Run(RuntimeOptions.Parse(args));
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"runtime failed: {exception}");

			return 1;
		}
	}

	private static int Run(RuntimeOptions options)
	{
		var manifestPath = options.Manifest ?? Path.Combine(AppContext.BaseDirectory, "Content", "bootstrap.wolfmanifest");
		using var catalog = new WolfPackCatalog(manifestPath);

		// Checked before loading code from the packs, which is only built for the target they were cooked for.
		RuntimeTarget.EnsureCanRun(catalog.Manifest.Target);
		if (options.Frames > 0)
		{
			Screen.VSyncEnabled = false;
		}

		var gameplaySymbols = catalog.Manifest.GameplaySymbolsId == Guid.Empty
			? null
			: catalog.Read(catalog.Manifest.GameplaySymbolsId);
		var (gameplayAssembly, gameplay) = new GameplayAssemblyLoader().Load(
			catalog.Read(catalog.Manifest.GameplayAssemblyId),
			gameplaySymbols);

		using var host = RuntimeHost.Create(new RuntimeHostOptions
		{
			Catalog = catalog,
			Gameplay = gameplay,
			ComponentAssemblies = [gameplayAssembly, typeof(ServiceCollectionExtensions).Assembly],
			ConfigureServices = services => services.AddWolfEngineGameplayUi(),
			FrameCount = options.Frames
		});

		var running = true;
		Exception? gameError = null;
		var gameThread = new Thread(() =>
		{
			try
			{
				while (Volatile.Read(ref running) && host.Tick())
				{
					Thread.Sleep(0);
				}
			}
			catch (Exception exception)
			{
				gameError = exception;
				Console.Error.WriteLine($"runtime game loop failed: {exception}");
			}
			finally
			{
				try
				{
					host.Stop();
				}
				catch (Exception exception)
				{
					gameError ??= exception;
					Console.Error.WriteLine($"runtime gameplay cleanup failed: {exception}");
				}
			}
		})
		{
			IsBackground = true,
			Name = "GameThread"
		};
		gameThread.Start();

		try
		{
			host.RenderPipeline.Run(() => Console.WriteLine("runtime renderer ready"));
		}
		finally
		{
			Volatile.Write(ref running, false);

			// Teardown can dispatch GPU retirement after the render loop exits. Service it until the game thread stops.
			var dispatcher = host.Services.GetRequiredService<IMainThreadDispatcher>();
			while (gameThread.Join(1) == false)
			{
				dispatcher.ExecutePending();
			}
		}

		if (gameError is not null)
		{
			throw gameError;
		}

		return 0;
	}

	private readonly record struct RuntimeOptions(string? Manifest, int Frames)
	{
		public static RuntimeOptions Parse(string[] args)
		{
			string? manifest = null;
			var frames = 0;
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] == "--manifest" && ++i < args.Length)
				{
					manifest = Path.GetFullPath(args[i]);
				}
				else if (args[i] == "--frames" && ++i < args.Length && int.TryParse(args[i], out frames) && frames > 0)
				{
				}
				else if (args[i] == "--quit")
				{
				}
				else
				{
					throw new ArgumentException($"Unknown or invalid argument '{args[i]}'.");
				}
			}

			return new RuntimeOptions(manifest, frames);
		}
	}
}
