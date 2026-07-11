using System.Diagnostics;
using Wolfie.IAE.Projects;

namespace Wolfie.IAE.ExternalTools;

public sealed class BlenderLauncher
{
	public void Open(string projectFile, string relativeSourcePath, string? configuredBlenderPath)
	{
		var source = ResolveManagedSource(projectFile, relativeSourcePath);
		if (!string.Equals(Path.GetExtension(source), ".blend", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Only Blender source files can be opened in Blender.");
		if (!File.Exists(source)) throw new FileNotFoundException("The managed Blender source file is missing.", source);
		if (!File.Exists(source + ".meta"))
			throw new InvalidOperationException("The Blender file is not a managed Wolfie asset.");
		if (string.IsNullOrWhiteSpace(configuredBlenderPath))
			throw new InvalidOperationException("Configure the Blender path in Edit > Preferences first.");
		var blender = WolfiePath.NormalizeAbsolute(configuredBlenderPath);
		if (!File.Exists(blender) && !Directory.Exists(blender))
			throw new InvalidOperationException("The configured Blender path no longer exists. Update it in Edit > Preferences.");

		var process = Process.Start(CreateStartInfo(blender, source));
		if (process is null) throw new InvalidOperationException("Blender could not be started.");
	}

	public static ProcessStartInfo CreateStartInfo(string blenderPath, string sourcePath)
	{
		if (OperatingSystem.IsMacOS() && Directory.Exists(blenderPath) &&
		    string.Equals(Path.GetExtension(blenderPath), ".app", StringComparison.OrdinalIgnoreCase))
		{
			var info = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
			info.ArgumentList.Add("-a");
			info.ArgumentList.Add(blenderPath);
			info.ArgumentList.Add(sourcePath);
			return info;
		}

		var direct = new ProcessStartInfo(blenderPath) { UseShellExecute = false };
		direct.ArgumentList.Add(sourcePath);
		return direct;
	}

	private static string ResolveManagedSource(string projectFile, string relativeSourcePath)
	{
		var relative = relativeSourcePath.Replace('\\', '/');
		if (!relative.StartsWith("Assets/", StringComparison.Ordinal) || relative.Contains("../", StringComparison.Ordinal))
			throw new ArgumentException("The managed source path is invalid.", nameof(relativeSourcePath));
		var projectRoot = Path.GetDirectoryName(WolfiePath.NormalizeAbsolute(projectFile))!;
		var assetsRoot = Path.Combine(projectRoot, "Assets");
		var source = WolfiePath.NormalizeAbsolute(Path.Combine(projectRoot,
			relative.Replace('/', Path.DirectorySeparatorChar)));
		if (!WolfiePath.IsWithin(source, assetsRoot)) throw new ArgumentException("The managed source path escapes Assets.");
		return source;
	}
}
