using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Logging;
using WolfEngine.Mathematics;
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
		// Set up before the arguments are parsed, so a bad argument is logged and shown like any other failure.
		using var log = RuntimeLog.Start(
			args,
			openConsole: args.Contains("--console"),
			showDialogs: args.Contains("--frames") == false);
		try
		{
			var exitCode = Run(RuntimeOptions.Parse(args));
			Console.WriteLine($"runtime exited with code {exitCode}");

			return exitCode;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"runtime failed: {exception}");
			log.Flush();
			log.ShowFatalError(exception);

			return 1;
		}
	}

	private static int Run(RuntimeOptions options)
	{
		var manifestPath = options.Manifest ?? Path.Combine(AppContext.BaseDirectory, "Content", "bootstrap.wolfmanifest");
		using var catalog = new WolfPackCatalog(manifestPath);

		// Checked before loading code from the packs, which is only built for the target they were cooked for.
		RuntimeTarget.EnsureCanRun(catalog.Manifest.Target);

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
			ConfigureServices = services =>
			{
				services.AddWolfEngineGameplayUi();
				services.AddSingleton<ILogSink, ConsoleLogSink>();
			},
			FrameCount = options.Frames,
			ShowSplashScreen = true,
			ConfigureGraphicsSettings = options.ApplyTo
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

	/// <param name="Manifest">The bootstrap manifest to run, when not the one next to the executable.</param>
	/// <param name="Frames">For automated runs: how many fixed-step frames to run before quitting, or zero.</param>
	/// <param name="Windowed">Starts in a window even when the project starts fullscreen.</param>
	/// <param name="WindowSize">The window size in points, which implies <paramref name="Windowed"/>, or zero.</param>
	private readonly record struct RuntimeOptions(string? Manifest, int Frames, bool Windowed, Int2 WindowSize)
	{
		/// <summary>
		/// Automated runs render as fast as they can, without vsync. Overrides from the command line win over the
		/// project's settings.
		/// </summary>
		public GraphicsSettings ApplyTo(CookedRuntimeSettings project, GraphicsSettings settings)
		{
			if (Frames > 0)
			{
				settings = settings with { VSync = false };
			}

			if (WindowSize.X > 0 && WindowSize.Y > 0)
			{
				return settings with
				{
					WindowMode = WindowMode.Windowed,
					Resolution = WindowSize
				};
			}

			if (Windowed && settings.WindowMode != WindowMode.Windowed)
			{
				return settings with
				{
					WindowMode = WindowMode.Windowed,
					Resolution = new Int2(project.Width, project.Height)
				};
			}

			return settings;
		}

		public static RuntimeOptions Parse(string[] args)
		{
			string? manifest = null;
			var frames = 0;
			var windowed = false;
			var windowSize = Int2.Zero;
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] == "--manifest" && ++i < args.Length)
				{
					manifest = Path.GetFullPath(args[i]);
				}
				else if (args[i] == "--frames" && ++i < args.Length && int.TryParse(args[i], out frames) && frames > 0)
				{
				}
				else if (args[i] == "--windowed")
				{
					windowed = true;
				}
				else if (args[i] == "--window-size" && ++i < args.Length && TryParseSize(args[i], out windowSize))
				{
				}
				else if (args[i] is "--quit" or "--console")
				{
					// --console is handled before parsing, by RuntimeLog.
				}
				else
				{
					throw new ArgumentException($"Unknown or invalid argument '{args[i]}'.");
				}
			}

			return new RuntimeOptions(manifest, frames, windowed, windowSize);
		}

		/// <summary>Reads a size written as <c>WIDTHxHEIGHT</c>, such as <c>1280x720</c>.</summary>
		private static bool TryParseSize(string value, out Int2 size)
		{
			size = Int2.Zero;
			var parts = value.Split('x');
			if (parts.Length != 2)
			{
				return false;
			}

			if (int.TryParse(parts[0], out var width) == false ||
			    int.TryParse(parts[1], out var height) == false ||
			    width <= 0 ||
			    height <= 0)
			{
				return false;
			}

			size = new Int2(width, height);

			return true;
		}
	}
}
