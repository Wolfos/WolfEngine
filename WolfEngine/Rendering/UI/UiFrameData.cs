using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.UI;

public static class UiTextureIds
{
	public static readonly nint FontAtlas = unchecked((nint)(-2));

	/// <summary>
	/// Views draw into the UI through a sentinel rather than a real texture id, because the UI frame is
	/// built on the game thread before the render thread knows which texture a view resolved to. The render
	/// thread rewrites each sentinel to that view's output. Sentinels run downwards from
	/// <see cref="ViewportBase"/>, one per view: negative ids cannot collide with a packed bindless handle,
	/// and the block is bounded so it cannot run into anything else either.
	/// </summary>
	private const nint ViewportBase = -3;

	/// <summary>How many views can be submitted to one UI frame.</summary>
	public const int MaxViewports = 64;

	/// <summary>Sentinel for <see cref="RenderViewId.Primary"/>.</summary>
	public static readonly nint SceneViewport = ViewportBase;

	public static nint Viewport(RenderViewId view)
	{
		if (view.IsValid == false)
		{
			throw new ArgumentException("A viewport sentinel needs a valid view id.", nameof(view));
		}

		if (view.Index >= MaxViewports)
		{
			throw new ArgumentOutOfRangeException(
				nameof(view),
				$"View {view} is beyond the {MaxViewports} views one UI frame can carry.");
		}

		return ViewportBase - view.Index;
	}

	/// <summary>True when <paramref name="textureId"/> is a viewport sentinel, and which view it belongs to.</summary>
	public static bool TryGetViewport(nint textureId, out RenderViewId view)
	{
		var index = (int)(ViewportBase - textureId);
		if (textureId <= ViewportBase && index < MaxViewports)
		{
			view = RenderViewId.FromIndex(index);
			return true;
		}

		view = RenderViewId.None;
		return false;
	}

	/// <summary>True when <paramref name="textureId"/> is any viewport sentinel.</summary>
	public static bool IsViewport(nint textureId) => TryGetViewport(textureId, out _);
}

/// <summary>
/// Immutable snapshot of UI draw data produced on the game thread.
/// Flattened for easy upload on the render thread and independent of the UI producer.
/// </summary>
public sealed class UiFrameData
{
	public static readonly UiFrameData Empty = new();

	public int VertexCount { get; init; }
	public int IndexCount { get; init; }
	public int CommandCount { get; init; }
	public Vector2 DisplayPos { get; init; }
	public Vector2 DisplaySize { get; init; }
	public Vector2 FramebufferSize { get; init; }
	public float DeltaTime { get; init; }
	public bool HasFontAtlas { get; init; }

	public UiTextureAtlas FontAtlas { get; init; } = new UiTextureAtlas();

	public UiDrawCommand[] Commands { get; init; } = Array.Empty<UiDrawCommand>();
	public UiVertex[] Vertices { get; init; } = Array.Empty<UiVertex>();
	public uint[] Indices { get; init; } = Array.Empty<uint>();

	/// <summary>
	/// True when a command draws an image that has no GPU resources yet. A texture surface recorded in that state
	/// is missing the image, so it must be recorded again once the image arrives.
	/// </summary>
	public bool HasPendingImages()
	{
		for (var i = 0; i < CommandCount; i++)
		{
			if (Commands[i].IsWaitingForImage)
			{
				return true;
			}
		}

		return false;
	}

	private Action<UiFrameData>? _releaseAction;
	private Action<UiTextureAtlas>? _fontAtlasUploadedAction;
	private int _referenceCount;

	internal void SetRelease(Action<UiFrameData>? releaseAction)
	{
		_releaseAction = releaseAction;
		Volatile.Write(ref _referenceCount, releaseAction is null ? 0 : 1);
	}

	internal UiFrameData Retain()
	{
		if (Volatile.Read(ref _releaseAction) is not null)
		{
			Interlocked.Increment(ref _referenceCount);
		}
		return this;
	}

	internal void SetFontAtlasUploaded(Action<UiTextureAtlas>? fontAtlasUploadedAction)
	{
		_fontAtlasUploadedAction = fontAtlasUploadedAction;
	}

	internal void MarkFontAtlasUploaded()
	{
		_fontAtlasUploadedAction?.Invoke(FontAtlas);
		_fontAtlasUploadedAction = null;
	}

	public void Release()
	{
		if (Volatile.Read(ref _releaseAction) is null)
		{
			_fontAtlasUploadedAction = null;
			return;
		}

		if (Interlocked.Decrement(ref _referenceCount) != 0)
		{
			return;
		}

		var releaseAction = Interlocked.Exchange(ref _releaseAction, null);
		_fontAtlasUploadedAction = null;
		releaseAction?.Invoke(this);
	}
}

/// <summary>Backend-neutral UI vertex. The layout intentionally matches the UI graphics pipeline.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct UiVertex
{
	public UiVertex(Vector2 position, Vector2 uv, uint color)
	{
		Position = position;
		UV = uv;
		Color = color;
	}

	public Vector2 Position { get; }
	public Vector2 UV { get; }
	public uint Color { get; }
}

public sealed class UiTextureAtlas
{
	public int Width { get; init; }
	public int Height { get; init; }
	public byte[] PixelsRgba { get; init; } = Array.Empty<byte>();
}

public readonly struct UiDrawCommand
{
	public UiDrawCommand(int elemCount, int idxOffset, int vtxOffset, Vector4 clipRect, nint textureId,
		Texture? atlas = null, float distanceRange = 0, bool solid = false, bool opaque = false, Texture? image = null)
	{
		if (atlas is not null && image is not null)
		{
			throw new ArgumentException("A UI draw command samples either a font atlas or an image, not both.", nameof(image));
		}

		ElemCount = elemCount;
		IdxOffset = idxOffset;
		VtxOffset = vtxOffset;
		ClipRect = clipRect;
		TextureId = textureId;
		Atlas = atlas;
		DistanceRange = distanceRange;
		Solid = solid;
		Opaque = opaque;
		Image = image;
	}

	public int ElemCount { get; }
	public int IdxOffset { get; }
	public int VtxOffset { get; }
	public Vector4 ClipRect { get; }
	public nint TextureId { get; }
	public Texture? Atlas { get; }
	public float DistanceRange { get; }
	public bool Solid { get; }
	/// <summary>Draws the sampled texture with the vertex alpha only, ignoring the texture's own alpha.</summary>
	public bool Opaque { get; }

	/// <summary>
	/// A texture asset drawn as a colour image, such as an icon. Unlike <see cref="Atlas"/>, which the UI renderer
	/// creates and destroys, an image belongs to the asset system: the render graph uploads it like any other
	/// texture, and the UI only reads its resources. Until they exist the command is skipped.
	/// </summary>
	public Texture? Image { get; }

	/// <summary>The texture whose shader resource view this command samples, if any.</summary>
	public ITextureResources? SampledResources => Atlas?.Resources ?? Image?.Resources;

	/// <summary>True when this command draws an image whose GPU resources have not been created yet.</summary>
	public bool IsWaitingForImage => Image is not null && Image.Resources is null;

	/// <summary>
	/// The <c>sampleTexture</c> mode <c>imgui.slang</c> uses for this command. Shared by every backend so the modes
	/// cannot drift apart.
	/// </summary>
	/// <param name="rendererSamplesTexture">
	/// Whether the renderer samples <see cref="TextureId"/> at all. The gameplay UI renderer does not: it only
	/// samples atlases and images.
	/// </param>
	public uint ResolveSampleMode(bool rendererSamplesTexture)
	{
		if (Atlas is not null)
		{
			return UiSampleModes.DistanceField;
		}

		if (Image is not null)
		{
			return Image.IsSrgb
				? UiSampleModes.SrgbColor
				: UiSampleModes.Color;
		}

		if (Solid || rendererSamplesTexture == false)
		{
			return UiSampleModes.Solid;
		}

		return Opaque
			? UiSampleModes.OpaqueColor
			: UiSampleModes.Color;
	}
}

/// <summary>Values of <c>sampleTexture</c> in <c>imgui.slang</c>. Keep the two in sync.</summary>
public static class UiSampleModes
{
	public const uint Solid = 0;
	public const uint Color = 1;
	public const uint DistanceField = 2;
	public const uint OpaqueColor = 3;

	/// <summary>
	/// Colour from an sRGB texture. Sampling decodes it to linear, but the UI blends in sRGB space, so the shader
	/// encodes it back; otherwise every image would draw darker than its source.
	/// </summary>
	public const uint SrgbColor = 4;
}
