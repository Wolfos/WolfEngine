using Wolfie.IAE.Projects;

namespace Wolfie.IAE.UnityAssets;

public sealed class UnityAssetScanner
{
	public UnityAssetScanResult Scan(string unityProjectPath)
	{
		var assetsRoot = Path.Combine(WolfiePath.NormalizeAbsolute(unityProjectPath), "Assets");
		if (!Directory.Exists(assetsRoot)) throw new DirectoryNotFoundException($"Unity Assets directory was not found: {assetsRoot}");
		var warnings = new List<string>();
		return new UnityAssetScanResult(ScanDirectory(assetsRoot, assetsRoot, warnings), warnings);
	}

	private static UnityAssetEntry ScanDirectory(string directory, string assetsRoot, List<string> warnings)
	{
		var children = new List<UnityAssetEntry>();
		try
		{
			foreach (var childDirectory in Directory.EnumerateDirectories(directory))
			{
				// Do not follow links: their targets may escape Assets and directory links may form cycles.
				if (IsSymbolicLink(childDirectory)) continue;
				children.Add(ScanDirectory(childDirectory, assetsRoot, warnings));
			}
			foreach (var file in Directory.EnumerateFiles(directory))
			{
				if (string.Equals(Path.GetExtension(file), ".meta", StringComparison.OrdinalIgnoreCase)) continue;
				if (IsSymbolicLink(file)) continue;
				try
				{
					children.Add(new UnityAssetEntry(Path.GetFileName(file), Relative(file, assetsRoot),
						UnityAssetEntryType.File, Path.GetExtension(file), File.GetLastWriteTimeUtc(file), []));
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					warnings.Add($"Could not inspect '{file}': {exception.Message}");
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			warnings.Add($"Could not read '{directory}': {exception.Message}");
		}

		children.Sort((left, right) =>
		{
			var type = left.Type.CompareTo(right.Type);
			return type != 0 ? type : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
		});
		DateTime? modified = null;
		try { modified = Directory.GetLastWriteTimeUtc(directory); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
		return new UnityAssetEntry(Path.GetFileName(directory), Relative(directory, assetsRoot),
			UnityAssetEntryType.Folder, string.Empty, modified, children);
	}

	private static bool IsSymbolicLink(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
	}

	private static string Relative(string path, string root)
	{
		var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
		return relative == "." ? "Assets" : "Assets/" + relative;
	}
}
