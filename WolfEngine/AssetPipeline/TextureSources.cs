using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace WolfEngine.AssetPipeline;

/// <summary>
/// Resolves a project source path, such as <c>/Assets/UI/Potion.png</c>, to the texture asset imported from it.
/// The editor answers from its asset database; a built game answers from the paths its build packed.
/// </summary>
public interface ITextureSourceProvider
{
	bool TryResolve(string sourcePath, out Guid nodeId);
}

/// <summary>Finds and normalises the texture source paths gameplay UI refers to.</summary>
public static partial class TextureSourceReferences
{
	/// <summary>
	/// Turns <c>/Assets/UI/Potion.png</c> or <c>Assets/UI/Potion.png</c> into the project-relative
	/// <c>Assets/UI/Potion.png</c> the asset database indexes. Anything outside <c>Assets/</c>, or with <c>.</c> or
	/// <c>..</c> segments, is not a project source path.
	/// </summary>
	public static bool TryNormalize(string? path, out string normalized)
	{
		normalized = string.Empty;

		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		var result = path.Trim().Replace('\\', '/').TrimStart('/');
		if (result.StartsWith("Assets/", StringComparison.Ordinal) == false ||
		    result.Contains(':') ||
		    result.Split('/').Any(part => part is "" or "." or ".."))
		{
			return false;
		}

		normalized = result;
		return true;
	}

	/// <summary>
	/// Paths in <c>url()</c> values of <c>background</c> and <c>background-image</c> declarations. An asset
	/// reference such as <c>url(asset:…)</c> is not a path and is skipped.
	/// </summary>
	public static IEnumerable<string> FindInCss(string css)
	{
		css = CssCommentPattern().Replace(css, string.Empty);

		foreach (Match declaration in CssBackgroundPattern().Matches(css))
		{
			foreach (Match url in CssUrlPattern().Matches(declaration.Groups["value"].Value))
			{
				if (TryNormalize(url.Groups["path"].Value, out var path))
				{
					yield return path;
				}
			}
		}
	}

	/// <summary>
	/// Every project path written as a literal in an assembly, including inside the markup strings Razor compiles
	/// a component's static HTML to. A path built at run time, such as <c>$"/Assets/Icons/{name}.png"</c>, does not
	/// appear whole and is not found.
	/// </summary>
	public static IEnumerable<string> FindInAssembly(string assemblyPath)
	{
		using var stream = File.OpenRead(assemblyPath);
		using var peReader = new PEReader(stream);
		var metadata = peReader.GetMetadataReader();
		var found = new HashSet<string>(StringComparer.Ordinal);
		var handle = MetadataTokens.UserStringHandle(1);

		while (handle.IsNil == false)
		{
			var text = metadata.GetUserString(handle);

			foreach (Match match in LiteralPathPattern().Matches(text))
			{
				if (TryNormalize(match.Value, out var path))
				{
					found.Add(path);
				}
			}

			handle = metadata.GetNextHandle(handle);
		}

		return found;
	}

	[GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
	private static partial Regex CssCommentPattern();

	[GeneratedRegex(@"background(-image)?\s*:(?<value>[^;{}]*)", RegexOptions.IgnoreCase)]
	private static partial Regex CssBackgroundPattern();

	[GeneratedRegex("url\\(\\s*['\"]?(?<path>[^'\")]+)['\"]?\\s*\\)", RegexOptions.IgnoreCase)]
	private static partial Regex CssUrlPattern();

	[GeneratedRegex(@"(?<![\w./-])/?Assets/[^""'\s()<>;,]+")]
	private static partial Regex LiteralPathPattern();
}

public sealed class WolfPackTextureSourceProvider(WolfPackCatalog catalog) : ITextureSourceProvider
{
	public bool TryResolve(string sourcePath, out Guid nodeId)
	{
		nodeId = Guid.Empty;

		if (TextureSourceReferences.TryNormalize(sourcePath, out var path) == false)
		{
			return false;
		}

		return catalog.Manifest.TextureSources.TryGetValue(path, out nodeId);
	}
}
