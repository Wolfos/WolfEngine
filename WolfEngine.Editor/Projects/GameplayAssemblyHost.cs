using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using WolfEngine.AssetPipeline;
using WolfEngine.Gameplay;

namespace WolfEngine.Editor.Projects;

public sealed class GameplayBuildResult
{
	public required bool Succeeded { get; init; }
	public required string Output { get; init; }
	public string? ShadowAssemblyPath { get; init; }
	public string? ShadowDirectoryPath { get; init; }
}

public sealed class GameplayLoadResult
{
	public long Generation { get; init; }
	public Assembly? Assembly { get; init; }
	public IGameplayModule? Module { get; init; }
	public WeakReference? UnloadedContextReference { get; init; }
}

public interface IGameplayAssemblyHost
{
	long CurrentGeneration { get; }
	bool IsBuildInProgress { get; }
	Assembly? CurrentAssembly { get; }
	IGameplayModule? CurrentModule { get; }

	bool RequestBuildAndReload();
	bool TryConsumeBuildResult(out GameplayBuildResult result);
	GameplayLoadResult EnsureLoaded();
	GameplayLoadResult ApplyPreparedBuild(GameplayBuildResult result);
	void UnloadCurrent();
}

public sealed class GameplayAssemblyHost : IGameplayAssemblyHost
{
	private readonly Func<IEditorProjectService> _projectServiceAccessor;
	private readonly object _sync = new();
	private GameplayLoadedAssembly? _current;
	private GameplayBuildResult? _pendingBuildResult;
	private string? _loadedProjectRootPath;
	private long _generation;
	private bool _isBuildInProgress;

	private sealed class GameplayLoadedAssembly
	{
		public required string ProjectRootPath { get; init; }
		public required string ShadowDirectoryPath { get; init; }
		public required string ShadowAssemblyPath { get; init; }
		public required GameplayAssemblyLoadContext LoadContext { get; init; }
		public required Assembly Assembly { get; init; }
		public IGameplayModule? Module { get; init; }
	}

	private sealed class GameplayPendingUnload
	{
		public required WeakReference LoadContextReference { get; init; }
		public required string ShadowDirectoryPath { get; init; }
	}

	public GameplayAssemblyHost(Func<IEditorProjectService> projectServiceAccessor)
	{
		_projectServiceAccessor = projectServiceAccessor ?? throw new ArgumentNullException(nameof(projectServiceAccessor));
	}

	public long CurrentGeneration
	{
		get
		{
			lock (_sync)
			{
				return _generation;
			}
		}
	}

	public bool IsBuildInProgress
	{
		get
		{
			lock (_sync)
			{
				return _isBuildInProgress;
			}
		}
	}

	public Assembly? CurrentAssembly
	{
		get
		{
			lock (_sync)
			{
				return _current?.Assembly;
			}
		}
	}

	public IGameplayModule? CurrentModule
	{
		get
		{
			lock (_sync)
			{
				return _current?.Module;
			}
		}
	}

	public bool RequestBuildAndReload()
	{
		var projectService = GetProjectService();
		var gameplayProjectPath = projectService.GameplayProjectPath;
		var projectRootPath = projectService.ProjectRootPath;
		if (string.IsNullOrWhiteSpace(gameplayProjectPath) ||
		    string.IsNullOrWhiteSpace(projectRootPath) ||
		    File.Exists(gameplayProjectPath) == false)
		{
			return false;
		}

		lock (_sync)
		{
			if (_isBuildInProgress)
			{
				return false;
			}

			_isBuildInProgress = true;
		}

		var fullGameplayProjectPath = Path.GetFullPath(gameplayProjectPath);
		var fullProjectRootPath = Path.GetFullPath(projectRootPath);
		Task.Run(() => BuildAndPrepareShadowCopy(fullProjectRootPath, fullGameplayProjectPath));
		return true;
	}

	public bool TryConsumeBuildResult(out GameplayBuildResult result)
	{
		lock (_sync)
		{
			if (_pendingBuildResult is null)
			{
				result = null!;
				return false;
			}

			result = _pendingBuildResult;
			_pendingBuildResult = null;
			return true;
		}
	}

	public GameplayLoadResult EnsureLoaded()
	{
		var projectService = GetProjectService();
		var gameplayProjectPath = projectService.GameplayProjectPath;
		var projectRootPath = projectService.ProjectRootPath;
		if (string.IsNullOrWhiteSpace(projectRootPath) || string.IsNullOrWhiteSpace(gameplayProjectPath))
		{
			return new GameplayLoadResult
			{
				Generation = CurrentGeneration
			};
		}

		lock (_sync)
		{
			if (_current is not null &&
			    string.Equals(_loadedProjectRootPath, Path.GetFullPath(projectRootPath), StringComparison.OrdinalIgnoreCase))
			{
				return CreateLoadResult(_generation, _current, unloadedContextReference: null);
			}
		}

		var gameplayAssemblyPath = ProjectTypeCatalog.TryFindGameplayAssemblyPath(gameplayProjectPath, GetCurrentBuildConfiguration());
		if (string.IsNullOrWhiteSpace(gameplayAssemblyPath))
		{
			return new GameplayLoadResult
			{
				Generation = CurrentGeneration
			};
		}

		var preparedBuild = PrepareShadowCopy(Path.GetFullPath(projectRootPath), gameplayAssemblyPath);
		return ApplyPreparedBuild(preparedBuild);
	}

	public GameplayLoadResult ApplyPreparedBuild(GameplayBuildResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		if (result.Succeeded == false || string.IsNullOrWhiteSpace(result.ShadowAssemblyPath) || string.IsNullOrWhiteSpace(result.ShadowDirectoryPath))
		{
			return new GameplayLoadResult
			{
				Generation = CurrentGeneration
			};
		}

		GameplayPendingUnload? pendingUnload;
		lock (_sync)
		{
			pendingUnload = UnloadCurrent_NoLock();
			var next = LoadShadowAssembly_NoLock(result.ShadowDirectoryPath, result.ShadowAssemblyPath);
			_current = next;
			_loadedProjectRootPath = GetProjectService().ProjectRootPath is { } projectRootPath
				? Path.GetFullPath(projectRootPath)
				: null;
			_generation++;
			StartUnloadCleanup(pendingUnload);
			return CreateLoadResult(_generation, next, pendingUnload?.LoadContextReference);
		}
	}

	public void UnloadCurrent()
	{
		GameplayPendingUnload? pendingUnload;
		lock (_sync)
		{
			pendingUnload = UnloadCurrent_NoLock();
			_loadedProjectRootPath = null;
		}

		WaitForUnloadAndDeleteShadowDirectory(pendingUnload);
	}

	private void BuildAndPrepareShadowCopy(string projectRootPath, string gameplayProjectPath)
	{
		GameplayBuildResult buildResult;
		try
		{
			var configuration = GetCurrentBuildConfiguration();
			var startInfo = new ProcessStartInfo("dotnet", $"build \"{gameplayProjectPath}\" --configuration {configuration}")
			{
				WorkingDirectory = projectRootPath,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};

			using var process = Process.Start(startInfo);
			if (process is null)
			{
				buildResult = new GameplayBuildResult
				{
					Succeeded = false,
					Output = "Failed to start gameplay build process."
				};
			}
			else
			{
				var buildStopwatch = Stopwatch.StartNew();
				var standardOutput = process.StandardOutput.ReadToEnd();
				var standardError = process.StandardError.ReadToEnd();
				process.WaitForExit();
				buildStopwatch.Stop();
				Console.WriteLine($"[GameplayReload] dotnet build: {buildStopwatch.Elapsed.TotalMilliseconds:F1} ms (exit {process.ExitCode}).");

				var output = string.Concat(standardOutput, string.IsNullOrWhiteSpace(standardError) ? string.Empty : Environment.NewLine + standardError).Trim();
				if (process.ExitCode != 0)
				{
					buildResult = new GameplayBuildResult
					{
						Succeeded = false,
						Output = string.IsNullOrWhiteSpace(output)
							? $"Gameplay build failed with exit code {process.ExitCode}."
							: output
					};
				}
				else
				{
					var gameplayAssemblyPath = ProjectTypeCatalog.TryFindGameplayAssemblyPath(gameplayProjectPath, configuration);
					buildResult = string.IsNullOrWhiteSpace(gameplayAssemblyPath)
						? new GameplayBuildResult
						{
							Succeeded = false,
							Output = string.IsNullOrWhiteSpace(output)
								? "Gameplay build succeeded, but the output assembly could not be located."
								: output
						}
						: PrepareShadowCopy(projectRootPath, gameplayAssemblyPath, output);
				}
			}
		}
		catch (Exception exception)
		{
			buildResult = new GameplayBuildResult
			{
				Succeeded = false,
				Output = exception.ToString()
			};
		}

		lock (_sync)
		{
			_pendingBuildResult = buildResult;
			_isBuildInProgress = false;
		}
	}

	private GameplayBuildResult PrepareShadowCopy(string projectRootPath, string gameplayAssemblyPath, string? buildOutput = null)
	{
		var sourceDirectory = Path.GetDirectoryName(gameplayAssemblyPath)
		                      ?? throw new InvalidOperationException($"Gameplay assembly '{gameplayAssemblyPath}' does not have a parent directory.");
		var libraryRoot = global::WolfEngine.AssetPipeline.AssetPipelinePaths.GetLibraryPath(projectRootPath);
		var loadsRoot = Path.Combine(libraryRoot, "Gameplay", "loads");
		Directory.CreateDirectory(loadsRoot);

		var shadowDirectory = Path.Combine(loadsRoot, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
		CopyDirectory(sourceDirectory, shadowDirectory);

		var shadowAssemblyPath = Path.Combine(shadowDirectory, Path.GetFileName(gameplayAssemblyPath));
		if (File.Exists(shadowAssemblyPath) == false)
		{
			throw new FileNotFoundException("Shadow-copied gameplay assembly was not found.", shadowAssemblyPath);
		}

		return new()
		{
			Succeeded = true,
			Output = buildOutput ?? string.Empty,
			ShadowAssemblyPath = shadowAssemblyPath,
			ShadowDirectoryPath = shadowDirectory
		};
	}

	private GameplayLoadedAssembly LoadShadowAssembly_NoLock(string shadowDirectoryPath, string shadowAssemblyPath)
	{
		var loadContext = new GameplayAssemblyLoadContext(shadowAssemblyPath);
		var assembly = loadContext.LoadManagedAssembly(shadowAssemblyPath);
		var module = TryCreateModule(assembly);

		return new GameplayLoadedAssembly
		{
			ProjectRootPath = GetProjectService().ProjectRootPath is { } projectRootPath
				? Path.GetFullPath(projectRootPath)
				: string.Empty,
			ShadowDirectoryPath = shadowDirectoryPath,
			ShadowAssemblyPath = shadowAssemblyPath,
			LoadContext = loadContext,
			Assembly = assembly,
			Module = module
		};
	}

	private GameplayPendingUnload? UnloadCurrent_NoLock()
	{
		if (_current is null)
		{
			return null;
		}

		var pendingUnload = new GameplayPendingUnload
		{
			LoadContextReference = new WeakReference(_current.LoadContext),
			ShadowDirectoryPath = _current.ShadowDirectoryPath
		};
		_current.LoadContext.Unload();
		_current = null;
		return pendingUnload;
	}

	private static GameplayLoadResult CreateLoadResult(long generation, GameplayLoadedAssembly? current, WeakReference? unloadedContextReference)
	{
		return new GameplayLoadResult
		{
			Generation = generation,
			Assembly = current?.Assembly,
			Module = current?.Module,
			UnloadedContextReference = unloadedContextReference
		};
	}

	private static IGameplayModule? TryCreateModule(Assembly assembly)
	{
		var entrypointType = ProjectTypeResolverUtility.GetLoadableTypes(assembly)
			.FirstOrDefault(candidate => string.Equals(candidate.Name, "GameplayEntrypoint", StringComparison.Ordinal));
		if (entrypointType is null)
		{
			return null;
		}

		var createMethod = entrypointType.GetMethod(
			"CreateModule",
			BindingFlags.Public | BindingFlags.Static,
			binder: null,
			types: Type.EmptyTypes,
			modifiers: null);
		if (createMethod is null || typeof(IGameplayModule).IsAssignableFrom(createMethod.ReturnType) == false)
		{
			return null;
		}

		return (IGameplayModule?)createMethod.Invoke(null, null);
	}

	private static void CopyDirectory(string sourceDirectoryPath, string destinationDirectoryPath)
	{
		Directory.CreateDirectory(destinationDirectoryPath);
		foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectoryPath, "*", SearchOption.AllDirectories))
		{
			var relativePath = Path.GetRelativePath(sourceDirectoryPath, directoryPath);
			Directory.CreateDirectory(Path.Combine(destinationDirectoryPath, relativePath));
		}

		foreach (var filePath in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
		{
			var relativePath = Path.GetRelativePath(sourceDirectoryPath, filePath);
			var destinationFilePath = Path.Combine(destinationDirectoryPath, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
			File.Copy(filePath, destinationFilePath, overwrite: true);
		}
	}

	private static void TryDeleteShadowDirectory(string shadowDirectoryPath)
	{
		if (string.IsNullOrWhiteSpace(shadowDirectoryPath) || Directory.Exists(shadowDirectoryPath) == false)
		{
			return;
		}

		try
		{
			Directory.Delete(shadowDirectoryPath, recursive: true);
		}
		catch
		{
		}
	}

	private static void StartUnloadCleanup(GameplayPendingUnload? pendingUnload)
	{
		if (pendingUnload is null)
		{
			return;
		}

		Task.Run(() => WaitForUnloadAndDeleteShadowDirectory(pendingUnload));
	}

	private static void WaitForUnloadAndDeleteShadowDirectory(GameplayPendingUnload? pendingUnload)
	{
		if (pendingUnload is null)
		{
			return;
		}

		WaitForUnload(pendingUnload.LoadContextReference);
		TryDeleteShadowDirectory(pendingUnload.ShadowDirectoryPath);
	}

	private static void WaitForUnload(WeakReference unloadedContextReference)
	{
		for (var attempt = 0; attempt < 10 && unloadedContextReference.IsAlive; attempt++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	private IEditorProjectService GetProjectService()
	{
		return _projectServiceAccessor();
	}

	private static string GetCurrentBuildConfiguration()
	{
		var configuration = typeof(GameplayAssemblyHost).Assembly
			.GetCustomAttribute<AssemblyConfigurationAttribute>()?
			.Configuration;
		return string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration;
	}
}
