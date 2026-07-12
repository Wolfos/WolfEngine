using System.Diagnostics;
using Wolfie.IAE.ManagedAssets;
using Wolfie.IAE.Projects;

namespace Wolfie.IAE.ExternalTools;

public interface IBlenderModelPublisher
{
	Task PublishAsync(WolfieProject project, string projectFile, string relativeSourcePath,
		string? configuredBlenderPath, CancellationToken cancellationToken = default);
}

public sealed class BlenderModelPublisher(ManagedAssetService managedAssets, IBlenderExportProcess process) : IBlenderModelPublisher
{
	private readonly SemaphoreSlim _publishGate = new(1, 1);

	public async Task PublishAsync(WolfieProject project, string projectFile, string relativeSourcePath,
		string? configuredBlenderPath, CancellationToken cancellationToken = default)
	{
		await _publishGate.WaitAsync(cancellationToken);
		try { await PublishCoreAsync(project, projectFile, relativeSourcePath, configuredBlenderPath, cancellationToken); }
		finally { _publishGate.Release(); }
	}

	private async Task PublishCoreAsync(WolfieProject project, string projectFile, string relativeSourcePath,
		string? configuredBlenderPath, CancellationToken cancellationToken)
	{
		var asset = managedAssets.Get(projectFile, relativeSourcePath);
		if (!string.Equals(Path.GetExtension(asset.SourcePath), ".blend", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Publish Model requires a managed Blender source file.");
		var output = asset.Outputs.SingleOrDefault(item =>
			string.Equals(Path.GetExtension(item.Path), ".fbx", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException("The managed Blender asset has no registered FBX output.");
		var blender = ResolveBlenderExecutable(configuredBlenderPath);
		var projectRoot = Path.GetDirectoryName(WolfiePath.NormalizeAbsolute(projectFile))!;
		var source = Path.Combine(projectRoot, asset.SourcePath.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(source)) throw new FileNotFoundException("The managed Blender source file is missing.", source);
		var jobDirectory = Path.Combine(projectRoot, "Cache", "Exports", asset.SourceId.ToString("N"), Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(jobDirectory);
		var temporaryOutput = Path.Combine(jobDirectory, Path.GetFileName(output.Path));
		try
		{
			var result = await process.ExportAsync(blender, source, temporaryOutput, cancellationToken);
			if (result.ExitCode != 0)
				throw new InvalidOperationException(BuildFailureMessage(result));
			if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length < 64)
				throw new InvalidOperationException("Blender reported success but did not produce a valid FBX file.");
			// Persist an existing Unity identity before the output commit. Nothing after the atomic
			// replacement may fail, otherwise a successful publish could be reported as failed.
			managedAssets.RefreshUnityGuids(project, projectFile, asset);
			await using var stream = new FileStream(temporaryOutput, FileMode.Open, FileAccess.Read, FileShare.Read);
			managedAssets.PublishOutput(project, asset, output.Path, stream);
		}
		finally
		{
			try { if (Directory.Exists(jobDirectory)) Directory.Delete(jobDirectory, true); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	public static string ResolveBlenderExecutable(string? configuredPath)
	{
		if (string.IsNullOrWhiteSpace(configuredPath))
			throw new InvalidOperationException("Configure the Blender path in Edit > Preferences first.");
		var path = WolfiePath.NormalizeAbsolute(configuredPath);
		if (OperatingSystem.IsMacOS() && Directory.Exists(path) &&
		    string.Equals(Path.GetExtension(path), ".app", StringComparison.OrdinalIgnoreCase))
			path = Path.Combine(path, "Contents", "MacOS", "Blender");
		if (!File.Exists(path)) throw new InvalidOperationException("The configured Blender executable could not be found.");
		return path;
	}

	private static string BuildFailureMessage(BlenderExportResult result)
	{
		var details = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
		if (details.Length > 1200) details = details[^1200..];
		return $"Blender export failed with exit code {result.ExitCode}. {details}".Trim();
	}
}

public interface IBlenderExportProcess
{
	Task<BlenderExportResult> ExportAsync(string blenderExecutable, string sourcePath, string outputPath,
		CancellationToken cancellationToken);
}

public sealed record BlenderExportResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class BlenderExportProcess : IBlenderExportProcess
{
	private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

	public async Task<BlenderExportResult> ExportAsync(string blenderExecutable, string sourcePath,
		string outputPath, CancellationToken cancellationToken)
	{
		var script = Path.Combine(AppContext.BaseDirectory, "ExternalTools", "Scripts", "wolfie_export_fbx.py");
		if (!File.Exists(script)) throw new FileNotFoundException("Wolfie's Blender export script is missing.", script);
		var info = new ProcessStartInfo(blenderExecutable)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in new[] { "--background", sourcePath, "--python", script, "--", "--output", outputPath })
			info.ArgumentList.Add(argument);
		using var process = Process.Start(info) ?? throw new InvalidOperationException("Blender could not be started.");
		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
		using var timeout = new CancellationTokenSource(Timeout);
		using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
		try { await process.WaitForExitAsync(combined.Token); }
		catch (OperationCanceledException)
		{
			try { process.Kill(true); } catch (InvalidOperationException) { }
			if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
				throw new TimeoutException("Blender export timed out after 10 minutes.");
			throw;
		}
		return new BlenderExportResult(process.ExitCode, await stdout, await stderr);
	}
}
