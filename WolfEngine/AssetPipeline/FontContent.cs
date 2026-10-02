using System.Text.RegularExpressions;

namespace WolfEngine.AssetPipeline;

/// <summary>Resolves authored source references to cooked font data. Never opens a source font at runtime.</summary>
public interface IFontContentProvider
{
	bool TryResolve(string sourcePath, out Guid assetId, out string revision);
	Stream Open(Guid assetId);
}

public static class FontSourceReferences
{
	public static string Normalize(string path)
	{
		var result = path.Trim().Replace('\\', '/').TrimStart('/');
		if (!result.StartsWith("Assets/", StringComparison.Ordinal) || result.Split('/').Any(p => p is ".." or ".") || result.Contains(':'))
			throw new ArgumentException("Font URLs must be project source paths such as /Assets/Fonts/Inter.ttf.", nameof(path));
		return result;
	}

	public static IEnumerable<string> Find(string css)
	{
		css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
		foreach (Match face in Regex.Matches(css, @"@font-face\s*\{(?<body>[^{}]*)\}", RegexOptions.IgnoreCase))
			foreach (Match url in Regex.Matches(face.Groups["body"].Value, "url\\(\\s*['\"]?(?<path>[^'\")]+)['\"]?\\s*\\)", RegexOptions.IgnoreCase))
				yield return Normalize(url.Groups["path"].Value);
	}
}

public sealed class WolfPackFontContentProvider(WolfPackCatalog catalog) : IFontContentProvider
{
	public bool TryResolve(string sourcePath, out Guid assetId, out string revision)
	{
		revision = "";
		if (!catalog.Manifest.FontSources.TryGetValue(FontSourceReferences.Normalize(sourcePath), out assetId)) return false;
		revision = catalog.GetEntry(assetId).Sha256;
		return true;
	}
	public Stream Open(Guid assetId) => catalog.OpenRead(assetId);
}
