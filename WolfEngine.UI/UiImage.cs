using System.Numerics;
using Facebook.Yoga;
using WolfEngine.AssetPipeline;

namespace WolfEngine.UI;

/// <summary>
/// Resolves the textures gameplay UI names: an <c>&lt;img&gt;</c> element's <c>src</c> and a
/// <c>background-image</c> <c>url()</c>.
/// </summary>
/// <remarks>
/// A reference is either a project path or an asset reference:
/// <list type="bullet">
/// <item><c>src="/Assets/UI/Potion.png"</c> names a texture source. A build packs every texture whose path appears
/// whole in the gameplay assembly or its CSS, so build a path at run time only from paths that also appear whole
/// somewhere.</item>
/// <item><c>src="@item.Icon"</c> binds an <see cref="AssetRef{T}"/>. Blazor turns every value on an HTML element
/// into a string, so it arrives as <c>asset:&lt;node id&gt;</c>, and the build packs it because the data that
/// holds the reference depends on it.</item>
/// </list>
/// </remarks>
internal sealed class UiImageSources(ITextureSourceProvider? paths = null)
{
	private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
	private readonly object _sync = new();

	public Texture? Resolve(UiNode node)
	{
		if (node.Attributes.TryGetValue("src", out var source) == false)
		{
			return null;
		}

		return Resolve(source as string);
	}

	public Texture? Resolve(string? reference)
	{
		if (string.IsNullOrEmpty(reference))
		{
			return null;
		}

		if (TryGetNodeId(reference, out var nodeId) == false)
		{
			return null;
		}

		try
		{
			var texture = AssetDatabase.GetInstance<Texture>(nodeId);
			if (texture is null)
			{
				Warn(reference, $"[Gameplay UI] Image '{reference}' names no loaded texture asset.");
			}

			return texture;
		}
		catch (InvalidOperationException exception)
		{
			Warn(reference, $"[Gameplay UI] Image '{reference}' could not be resolved to a texture: {exception.Message}");
			return null;
		}
	}

	private bool TryGetNodeId(string reference, out Guid nodeId)
	{
		if (AssetReferenceText.TryParse(reference, out nodeId))
		{
			return true;
		}

		if (TextureSourceReferences.TryNormalize(reference, out var path) == false)
		{
			Warn(
				reference,
				$"[Gameplay UI] Image '{reference}' is neither a project path such as /Assets/UI/Potion.png nor an asset reference.");
			return false;
		}

		if (paths is not null && paths.TryResolve(path, out nodeId))
		{
			return true;
		}

		Warn(reference, $"[Gameplay UI] Image '{reference}' names no texture in this project or build.");
		return false;
	}

	private void Warn(string key, string message)
	{
		lock (_sync)
		{
			if (_diagnostics.Add(key) == false)
			{
				return;
			}
		}

		Console.Error.WriteLine(message);
	}
}

/// <summary>Intrinsic sizing for <c>&lt;img&gt;</c>, kept apart from Yoga's native callback so it can be tested.</summary>
internal static class UiImageLayout
{
	/// <summary>
	/// The size Yoga should give an image whose texture is <paramref name="naturalWidth"/> by
	/// <paramref name="naturalHeight"/> pixels.
	/// </summary>
	/// <remarks>
	/// A side Yoga fixes (<see cref="MeasureMode.Exactly"/>) is kept, and the free side follows the
	/// aspect ratio, capped by an <see cref="MeasureMode.AtMost"/> limit. With neither side fixed
	/// the image takes its natural size, even when that overflows: like a browser, an image only shrinks when CSS
	/// asks it to.
	/// </remarks>
	public static Vector2 Measure(
		float naturalWidth,
		float naturalHeight,
		float width,
		MeasureMode widthMode,
		float height,
		MeasureMode heightMode)
	{
		var widthFixed = widthMode == MeasureMode.Exactly;
		var heightFixed = heightMode == MeasureMode.Exactly;

		if (widthFixed && heightFixed)
		{
			return new Vector2(width, height);
		}

		var hasAspectRatio = naturalWidth > 0.0f && naturalHeight > 0.0f;

		if (widthFixed)
		{
			var derivedHeight = hasAspectRatio
				? width * naturalHeight / naturalWidth
				: 0.0f;

			return new Vector2(width, Cap(derivedHeight, height, heightMode));
		}

		if (heightFixed)
		{
			var derivedWidth = hasAspectRatio
				? height * naturalWidth / naturalHeight
				: 0.0f;

			return new Vector2(Cap(derivedWidth, width, widthMode), height);
		}

		return new Vector2(naturalWidth, naturalHeight);
	}

	private static float Cap(float value, float limit, MeasureMode mode)
	{
		if (mode == MeasureMode.AtMost)
		{
			return MathF.Min(value, limit);
		}

		return value;
	}
}
