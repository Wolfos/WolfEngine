namespace Wolfie.IAE.Projects;

public static class WolfiePath
{
	public static string NormalizeAbsolute(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
	}

	public static bool IsWithin(string candidate, string parent)
	{
		var candidatePath = NormalizeAbsolute(candidate);
		var parentPath = NormalizeAbsolute(parent);
		if (string.Equals(candidatePath, parentPath, PathComparison)) return true;
		return candidatePath.StartsWith(parentPath + Path.DirectorySeparatorChar, PathComparison);
	}

	public static StringComparison PathComparison =>
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
}
