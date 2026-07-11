using Wolfie.IAE.Projects;
using Wolfie.IAE.ManagedAssets;

namespace Wolfie.IAE.UnityAssets;

public sealed class UnityAssetScanner
{
	public UnityAssetScanResult Scan(WolfieProject project, string projectFile, ManagedAssetService managedAssets)
	{
		var managed = managedAssets.LoadAll(projectFile);
		var scan = Scan(project.UnityProjectPath);
		var projectRoot = Path.GetDirectoryName(WolfiePath.NormalizeAbsolute(projectFile))!;
		var assets = ApplyOwnership(scan.Root, project.UnityProjectPath, managed);
		foreach (var record in managed.Values)
			assets = AddManagedSource(assets, projectRoot, record);
		var templatesRoot = Path.Combine(projectRoot, "Templates");
		if (!Directory.Exists(templatesRoot))
			throw new DirectoryNotFoundException($"Wolfie Templates directory was not found: {templatesRoot}");
		var templates = ScanWolfieDirectory(templatesRoot, templatesRoot, scan.Warnings);
		var workspace = new UnityAssetEntry(project.Name, string.Empty, UnityAssetEntryType.Folder,
			string.Empty, null, [assets, templates], true);
		return scan with { Root = workspace };
	}

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

	private static UnityAssetEntry ApplyOwnership(UnityAssetEntry entry, string unityRoot,
		IReadOnlyDictionary<string, ManagedAssetRecord> managed)
	{
		var children = entry.Children.Select(child => ApplyOwnership(child, unityRoot, managed)).ToArray();
		if (entry.Type == UnityAssetEntryType.File && managed.TryGetValue(entry.RelativePath, out var record))
		{
			var output = record.Outputs.FirstOrDefault(item =>
				string.Equals(item.Path, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
			var guid = output?.UnityGuid ?? ManagedAssetService.ReadUnityGuid(
				Path.Combine(unityRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
			return entry with { Children = children, IsManaged = true, ManagedAssetId = record.SourceId, UnityGuid = guid };
		}
		return entry with { Children = children, IsManaged = children.Any(child => child.IsManaged) };
	}

	private static UnityAssetEntry AddManagedSource(UnityAssetEntry root, string projectRoot, ManagedAssetRecord record)
	{
		var parts = record.SourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2 || !string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase)) return root;
		return AddManagedSource(root, projectRoot, record, parts, 1);
	}

	private static UnityAssetEntry AddManagedSource(UnityAssetEntry folder, string projectRoot,
		ManagedAssetRecord record, string[] parts, int index)
	{
		if (index == parts.Length - 1)
		{
			if (folder.Children.Any(child => string.Equals(child.RelativePath, record.SourcePath, StringComparison.OrdinalIgnoreCase)))
				return folder;
			var absolute = Path.Combine(projectRoot, record.SourcePath.Replace('/', Path.DirectorySeparatorChar));
			var file = new UnityAssetEntry(parts[index], record.SourcePath, UnityAssetEntryType.File,
				Path.GetExtension(parts[index]), File.Exists(absolute) ? File.GetLastWriteTimeUtc(absolute) : null,
				[], true, record.SourceId);
			return folder with { Children = SortChildren([.. folder.Children, file]), IsManaged = true };
		}

		var relativeFolder = string.Join('/', parts[..(index + 1)]);
		var children = folder.Children.ToList();
		var childIndex = children.FindIndex(child => child.Type == UnityAssetEntryType.Folder &&
			string.Equals(child.RelativePath, relativeFolder, StringComparison.OrdinalIgnoreCase));
		var child = childIndex >= 0 ? children[childIndex] : new UnityAssetEntry(parts[index], relativeFolder,
			UnityAssetEntryType.Folder, string.Empty, null, [], true);
		child = AddManagedSource(child, projectRoot, record, parts, index + 1);
		if (childIndex >= 0) children[childIndex] = child; else children.Add(child);
		return folder with { Children = SortChildren(children), IsManaged = true };
	}

	private static IReadOnlyList<UnityAssetEntry> SortChildren(IEnumerable<UnityAssetEntry> entries) => entries
		.OrderBy(entry => entry.Type).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();

	private static UnityAssetEntry ScanWolfieDirectory(string directory, string templatesRoot,
		IReadOnlyList<string> existingWarnings)
	{
		var warnings = existingWarnings as List<string> ?? [];
		var children = new List<UnityAssetEntry>();
		try
		{
			foreach (var childDirectory in Directory.EnumerateDirectories(directory))
				if (!IsSymbolicLink(childDirectory)) children.Add(ScanWolfieDirectory(childDirectory, templatesRoot, warnings));
			foreach (var file in Directory.EnumerateFiles(directory))
			{
				if (IsSymbolicLink(file)) continue;
				var relative = Path.GetRelativePath(templatesRoot, file).Replace(Path.DirectorySeparatorChar, '/');
				children.Add(new UnityAssetEntry(Path.GetFileName(file), "Templates/" + relative,
					UnityAssetEntryType.File, Path.GetExtension(file), File.GetLastWriteTimeUtc(file), [], true));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{ warnings.Add($"Could not read '{directory}': {exception.Message}"); }
		children.Sort((left, right) => left.Type != right.Type
			? left.Type.CompareTo(right.Type) : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
		var relativeDirectory = Path.GetRelativePath(templatesRoot, directory).Replace(Path.DirectorySeparatorChar, '/');
		var path = relativeDirectory == "." ? "Templates" : "Templates/" + relativeDirectory;
		return new UnityAssetEntry(Path.GetFileName(directory), path, UnityAssetEntryType.Folder,
			string.Empty, Directory.GetLastWriteTimeUtc(directory), children, true);
	}

	private static string Relative(string path, string root)
	{
		var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
		return relative == "." ? "Assets" : "Assets/" + relative;
	}
}
