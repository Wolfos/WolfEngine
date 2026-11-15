using System.Diagnostics;
using System.Reflection;
using WolfEngine.AssetPipeline;
using WolfEngine.Gameplay;

namespace WolfEngine.Editor.Projects;

public sealed class GameplayBuildResult
{
	public required bool Succeeded { get; init; }
	public required string Output { get; init; }
	public string? ShadowAssemblyPath { get; init; }
	public string? ShadowDirectoryPath { get; init; }

	/// <summary>The built assembly the shadow copy was taken from.</summary>
	public string? SourceAssemblyPath { get; init; }

	/// <summary>The gameplay source as scanned before the build, or null when nothing was built.</summary>
	public GameplaySourceSnapshot? Source { get; init; }
}

public record struct GameplayLoadResult
{
	public long Generation { get; init; }
	public Assembly? Assembly { get; init; }
	public IGameplayModule? Module { get; init; }
	public WeakReference? UnloadedContextReference { get; init; }

	/// <summary>
	/// The output of a rebuild that failed because the source had changed, when an older build was loaded instead.
	/// </summary>
	public string? BuildFailureOutput { get; init; }
}

public interface IGameplayAssemblyHost
{
	long CurrentGeneration { get; }
	bool IsBuildInProgress { get; }
	Assembly? CurrentAssembly { get; }
	IGameplayModule? CurrentModule { get; }
	string? LoadedAssemblyPath { get; }

	/// <summary>
	/// Whether the gameplay source has changed since the loaded assembly was built. Cheap to read every frame: it
	/// rescans the source only after a file watcher reported a change.
	/// </summary>
	bool IsSourceStale { get; }

	/// <summary>Whether the last build failed and the source has not changed since.</summary>
	bool LastBuildFailedForCurrentSource { get; }

	bool HasPendingBuildResult { get; }

	/// <summary>Rescans the gameplay source now, without relying on the file watcher.</summary>
	bool CheckSourceStale();

	bool RequestBuildAndReload();

	/// <summary>
	/// Builds on the calling thread and queues the result for the editor to apply, as
	/// <see cref="RequestBuildAndReload"/> does in the background. Returns false when a build is already running.
	/// </summary>
	bool BuildAndQueueReload();

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
	private string? _failedBuildFingerprint;
	private string? _currentSourceFingerprint;
	private bool _isSourceStale;
	private volatile bool _sourceStateDirty = true;
	private FileSystemWatcher? _sourceWatcher;
	private string? _watchedDirectoryPath;

	private sealed class GameplayLoadedAssembly
	{
		public required string ProjectRootPath { get; init; }
		public required string ShadowDirectoryPath { get; init; }
		public required string ShadowAssemblyPath { get; init; }
		public required string? SourceAssemblyPath { get; init; }
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

	public string? LoadedAssemblyPath
	{
		get
		{
			lock (_sync)
			{
				return _current?.SourceAssemblyPath;
			}
		}
	}

	public bool IsSourceStale
	{
		get
		{
			if (_sourceStateDirty)
			{
				RefreshSourceState();
			}

			lock (_sync)
			{
				return _isSourceStale;
			}
		}
	}

	public bool LastBuildFailedForCurrentSource
	{
		get
		{
			if (_sourceStateDirty)
			{
				RefreshSourceState();
			}

			lock (_sync)
			{
				return _failedBuildFingerprint is not null &&
				       string.Equals(_failedBuildFingerprint, _currentSourceFingerprint, StringComparison.Ordinal);
			}
		}
	}

	public bool HasPendingBuildResult
	{
		get
		{
			lock (_sync)
			{
				return _pendingBuildResult is not null;
			}
		}
	}

	public bool CheckSourceStale()
	{
		RefreshSourceState();
		lock (_sync)
		{
			return _isSourceStale;
		}
	}

	public bool BuildAndQueueReload()
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

		BuildAndPublishPendingResult(Path.GetFullPath(projectRootPath), Path.GetFullPath(gameplayProjectPath));
		return true;
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
		Task.Run(() => BuildAndPublishPendingResult(fullProjectRootPath, fullGameplayProjectPath));
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
		
		// No project built
		if (string.IsNullOrWhiteSpace(projectRootPath) || string.IsNullOrWhiteSpace(gameplayProjectPath))
		{
			return new()
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
		// No gameplay assembly was built yet, force a build
		if (string.IsNullOrWhiteSpace(gameplayAssemblyPath))
		{
			var buildResult = BuildAndPrepareShadowCopy(Path.GetFullPath(projectRootPath), Path.GetFullPath(gameplayProjectPath));
			if (buildResult.Succeeded == false)
			{
				throw new InvalidOperationException(string.IsNullOrWhiteSpace(buildResult.Output)
					? "Gameplay assembly is missing and gameplay build failed."
					: buildResult.Output);
			}

			return ApplyPreparedBuild(buildResult);
		}

		var fullProjectRootPath = Path.GetFullPath(projectRootPath);
		var fullGameplayProjectPath = Path.GetFullPath(gameplayProjectPath);
		var checkStopwatch = Stopwatch.StartNew();
		var source = GameplaySourceFreshness.Scan(fullGameplayProjectPath, fullProjectRootPath);
		var stamp = GameplaySourceFreshness.TryReadStamp(fullProjectRootPath);
		var isStale = GameplaySourceFreshness.IsStale(source, gameplayAssemblyPath, stamp);
		checkStopwatch.Stop();
		Console.WriteLine(
			$"[GameplayReload] Source check: {checkStopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
			$"{source.InputPaths.Count} inputs, {(isStale ? "stale" : "fresh")}.");

		if (isStale == false)
		{
			// Record the input list, so a file deleted before the next start is noticed.
			if (stamp is null ||
			    string.Equals(stamp.AssemblyPath, Path.GetFullPath(gameplayAssemblyPath), StringComparison.OrdinalIgnoreCase) == false ||
			    stamp.AssemblyWriteTimeUtc != File.GetLastWriteTimeUtc(gameplayAssemblyPath))
			{
				GameplaySourceFreshness.WriteStamp(fullProjectRootPath, GameplaySourceFreshness.CreateStamp(source, gameplayAssemblyPath));
			}

			return ApplyPreparedBuild(PrepareShadowCopy(fullProjectRootPath, gameplayAssemblyPath));
		}

		var rebuild = BuildAndPrepareShadowCopy(fullProjectRootPath, fullGameplayProjectPath);
		if (rebuild.Succeeded)
		{
			return ApplyPreparedBuild(rebuild);
		}

		// A scene loaded with the older build can lose what only the newer source declares, but refusing to open
		// the project over a compile error would be worse. The caller reports the failure.
		var fallback = ApplyPreparedBuild(PrepareShadowCopy(fullProjectRootPath, gameplayAssemblyPath));
		return fallback with
		{
			BuildFailureOutput = string.IsNullOrWhiteSpace(rebuild.Output)
				? "Gameplay build failed."
				: rebuild.Output
		};
	}

	public GameplayLoadResult ApplyPreparedBuild(GameplayBuildResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		if (result.Succeeded == false || string.IsNullOrWhiteSpace(result.ShadowAssemblyPath) || string.IsNullOrWhiteSpace(result.ShadowDirectoryPath))
		{
			return new()
			{
				Generation = CurrentGeneration
			};
		}

		GameplayPendingUnload? pendingUnload;
		lock (_sync)
		{
			pendingUnload = UnloadCurrent_NoLock();
			var next = LoadShadowAssembly_NoLock(result.ShadowDirectoryPath, result.ShadowAssemblyPath, result.SourceAssemblyPath);
			_current = next;
			_loadedProjectRootPath = GetProjectService().ProjectRootPath is { } projectRootPath
				? Path.GetFullPath(projectRootPath)
				: null;
			_generation++;
			if (result.Source is not null &&
			    string.IsNullOrWhiteSpace(result.SourceAssemblyPath) == false &&
			    _loadedProjectRootPath is not null)
			{
				GameplaySourceFreshness.WriteStamp(
					_loadedProjectRootPath,
					GameplaySourceFreshness.CreateStamp(result.Source, result.SourceAssemblyPath));
			}

			EnsureSourceWatcher_NoLock();
			_sourceStateDirty = true;
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
			DisposeSourceWatcher_NoLock();
			_failedBuildFingerprint = null;
			_sourceStateDirty = true;
		}

		WaitForUnloadAndDeleteShadowDirectory(pendingUnload);
	}

	private GameplayBuildResult BuildAndPrepareShadowCopy(string projectRootPath, string gameplayProjectPath)
	{
		// Scanned before the build, so a file saved while it runs reads as newer than the stamp.
		GameplaySourceSnapshot? source = null;
		try
		{
			source = GameplaySourceFreshness.Scan(gameplayProjectPath, projectRootPath);
		}
		catch (Exception exception)
		{
			Console.WriteLine($"[GameplayReload] Source scan before build failed: {exception.Message}");
		}

		var result = RunBuildAndPrepareShadowCopy(projectRootPath, gameplayProjectPath, source);
		RecordBuildOutcome(source, result.Succeeded);
		return result;
	}

	private GameplayBuildResult RunBuildAndPrepareShadowCopy(
		string projectRootPath,
		string gameplayProjectPath,
		GameplaySourceSnapshot? source)
	{
		try
		{
			var configuration = GetCurrentBuildConfiguration();
			var startInfo = new ProcessStartInfo("dotnet", $"build \"{gameplayProjectPath}\" --configuration {configuration} /m:1")
			{
				WorkingDirectory = projectRootPath,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};

			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return new()
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
					return new()
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
					return string.IsNullOrWhiteSpace(gameplayAssemblyPath)
						? new()
						{
							Succeeded = false,
							Output = string.IsNullOrWhiteSpace(output)
								? "Gameplay build succeeded, but the output assembly could not be located."
								: output
						}
						: PrepareShadowCopy(projectRootPath, gameplayAssemblyPath, output, source);
				}
			}
		}
		catch (Exception exception)
		{
			return new()
			{
				Succeeded = false,
				Output = exception.ToString()
			};
		}
	}

	private void BuildAndPublishPendingResult(string projectRootPath, string gameplayProjectPath)
	{
		var buildResult = BuildAndPrepareShadowCopy(projectRootPath, gameplayProjectPath);
		lock (_sync)
		{
			_pendingBuildResult = buildResult;
			_isBuildInProgress = false;
		}
	}

	private void RecordBuildOutcome(GameplaySourceSnapshot? source, bool succeeded)
	{
		lock (_sync)
		{
			_failedBuildFingerprint = succeeded
				? null
				: source?.Fingerprint;
		}

		_sourceStateDirty = true;
	}

	private void RefreshSourceState()
	{
		_sourceStateDirty = false;
		var projectService = GetProjectService();
		var gameplayProjectPath = projectService.GameplayProjectPath;
		var projectRootPath = projectService.ProjectRootPath;
		var loadedAssemblyPath = LoadedAssemblyPath;
		if (string.IsNullOrWhiteSpace(gameplayProjectPath) ||
		    string.IsNullOrWhiteSpace(projectRootPath) ||
		    string.IsNullOrWhiteSpace(loadedAssemblyPath) ||
		    File.Exists(gameplayProjectPath) == false)
		{
			lock (_sync)
			{
				_isSourceStale = false;
				_currentSourceFingerprint = null;
			}

			return;
		}

		try
		{
			var fullProjectRootPath = Path.GetFullPath(projectRootPath);
			var source = GameplaySourceFreshness.Scan(gameplayProjectPath, fullProjectRootPath);
			var stamp = GameplaySourceFreshness.TryReadStamp(fullProjectRootPath);
			var isStale = GameplaySourceFreshness.IsStale(source, loadedAssemblyPath, stamp);
			lock (_sync)
			{
				_isSourceStale = isStale;
				_currentSourceFingerprint = source.Fingerprint;
			}
		}
		catch (Exception exception)
		{
			// Directories can vanish mid-scan, as during a branch switch. Try again on the next read.
			Console.WriteLine($"[GameplayReload] Source check failed: {exception.Message}");
			_sourceStateDirty = true;
		}
	}

	private void EnsureSourceWatcher_NoLock()
	{
		var gameplayProjectPath = GetProjectService().GameplayProjectPath;
		var directoryPath = string.IsNullOrWhiteSpace(gameplayProjectPath)
			? null
			: Path.GetDirectoryName(Path.GetFullPath(gameplayProjectPath));
		if (_sourceWatcher is not null &&
		    string.Equals(_watchedDirectoryPath, directoryPath, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		DisposeSourceWatcher_NoLock();
		if (directoryPath is null || Directory.Exists(directoryPath) == false)
		{
			return;
		}

		try
		{
			var watcher = new FileSystemWatcher(directoryPath)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
			};
			watcher.Changed += (_, args) => OnSourceFileEvent(directoryPath, args.FullPath);
			watcher.Created += (_, args) => OnSourceFileEvent(directoryPath, args.FullPath);
			watcher.Deleted += (_, args) => OnSourceFileEvent(directoryPath, args.FullPath);
			watcher.Renamed += (_, args) =>
			{
				OnSourceFileEvent(directoryPath, args.OldFullPath);
				OnSourceFileEvent(directoryPath, args.FullPath);
			};
			watcher.Error += (_, _) => _sourceStateDirty = true;
			watcher.EnableRaisingEvents = true;
			_sourceWatcher = watcher;
			_watchedDirectoryPath = directoryPath;
		}
		catch (Exception exception)
		{
			// Without a watcher the toolbar only learns of changes after a build; scene loads still rescan.
			Console.WriteLine($"[GameplayReload] Failed to watch gameplay source '{directoryPath}': {exception.Message}");
		}
	}

	private void OnSourceFileEvent(string gameplayProjectDirectory, string path)
	{
		if (GameplaySourceFreshness.CanAffectBuild(gameplayProjectDirectory, path))
		{
			_sourceStateDirty = true;
		}
	}

	private void DisposeSourceWatcher_NoLock()
	{
		_sourceWatcher?.Dispose();
		_sourceWatcher = null;
		_watchedDirectoryPath = null;
	}

	private GameplayBuildResult PrepareShadowCopy(
		string projectRootPath,
		string gameplayAssemblyPath,
		string? buildOutput = null,
		GameplaySourceSnapshot? source = null)
	{
		var sourceDirectory = Path.GetDirectoryName(gameplayAssemblyPath)
		                      ?? throw new InvalidOperationException($"Gameplay assembly '{gameplayAssemblyPath}' does not have a parent directory.");
		var libraryRoot = AssetPipelinePaths.GetLibraryPath(projectRootPath);
		var loadsRoot = Path.Combine(libraryRoot, "Gameplay", "loads");
		Directory.CreateDirectory(loadsRoot);

		var shadowDirectory = Path.Combine(loadsRoot, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
		CopyDirectory(sourceDirectory, shadowDirectory);

		var shadowAssemblyPath = Path.Combine(shadowDirectory, Path.GetFileName(gameplayAssemblyPath));
		if (File.Exists(shadowAssemblyPath) == false)
		{
			throw new FileNotFoundException("Shadow-copied gameplay assembly was not found.", shadowAssemblyPath);
		}
		
		CleanupStaleShadowCopies(loadsRoot, shadowDirectory);

		return new()
		{
			Succeeded = true,
			Output = buildOutput ?? string.Empty,
			ShadowAssemblyPath = shadowAssemblyPath,
			ShadowDirectoryPath = shadowDirectory,
			SourceAssemblyPath = Path.GetFullPath(gameplayAssemblyPath),
			Source = source
		};
	}
	

	private GameplayLoadedAssembly LoadShadowAssembly_NoLock(string shadowDirectoryPath, string shadowAssemblyPath, string? sourceAssemblyPath)
	{
		var loadContext = new GameplayAssemblyLoadContext(shadowAssemblyPath);
		var assembly = loadContext.LoadManagedAssembly(shadowAssemblyPath);
		var module = TryCreateModule(assembly);

		return new()
		{
			ProjectRootPath = GetProjectService().ProjectRootPath is { } projectRootPath
				? Path.GetFullPath(projectRootPath)
				: string.Empty,
			ShadowDirectoryPath = shadowDirectoryPath,
			ShadowAssemblyPath = shadowAssemblyPath,
			SourceAssemblyPath = sourceAssemblyPath,
			LoadContext = loadContext,
			Assembly = assembly,
			Module = module
		};
	}
	
	private void CleanupStaleShadowCopies(string loadsRoot, string preparedDirectory)
	{
		var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			Path.GetFullPath(preparedDirectory)
		};

		lock (_sync)
		{
			if (_current?.ShadowDirectoryPath is { } current)
			{
				keep.Add(Path.GetFullPath(current));
			}

			if (_pendingBuildResult?.ShadowDirectoryPath is { } pending)
			{
				keep.Add(Path.GetFullPath(pending));
			}
		}

		foreach (var directory in Directory.EnumerateDirectories(loadsRoot))
		{
			if (keep.Contains(Path.GetFullPath(directory)))
			{
				continue;
			}

			TryDeleteShadowDirectory(directory);
		}
	}

	private GameplayPendingUnload? UnloadCurrent_NoLock()
	{
		if (_current is null)
		{
			return null;
		}

		var pendingUnload = new GameplayPendingUnload
		{
			LoadContextReference = new(_current.LoadContext),
			ShadowDirectoryPath = _current.ShadowDirectoryPath
		};
		_current.LoadContext.Unload();
		_current = null;
		return pendingUnload;
	}

	private static GameplayLoadResult CreateLoadResult(long generation, GameplayLoadedAssembly? current, WeakReference? unloadedContextReference)
	{
		return new()
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
