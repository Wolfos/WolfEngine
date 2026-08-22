using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering.UI;

public static class UiTextureIds
{
	public static readonly nint FontAtlas = unchecked((nint)(-2));
	public static readonly nint SceneViewport = unchecked((nint)(-3));
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
	public UiDrawCommand(int elemCount, int idxOffset, int vtxOffset, Vector4 clipRect, nint textureId)
	{
		ElemCount = elemCount;
		IdxOffset = idxOffset;
		VtxOffset = vtxOffset;
		ClipRect = clipRect;
		TextureId = textureId;
	}

	public int ElemCount { get; }
	public int IdxOffset { get; }
	public int VtxOffset { get; }
	public Vector4 ClipRect { get; }
	public nint TextureId { get; }
}
