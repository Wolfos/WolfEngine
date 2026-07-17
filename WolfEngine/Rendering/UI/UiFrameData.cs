using System;
using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Rendering.UI;

public static class UiTextureIds
{
	public static readonly nint FontAtlas = unchecked((nint)(-2));
	public static readonly nint SceneViewport = unchecked((nint)(-3));
}

/// <summary>
/// Immutable snapshot of ImGui draw data produced on the game thread.
/// Flattened for easy upload on the render thread.
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

	public ImGuiFontAtlas FontAtlas { get; init; } = new ImGuiFontAtlas();

	public UiDrawCommand[] Commands { get; init; } = Array.Empty<UiDrawCommand>();
	public ImDrawVert[] Vertices { get; init; } = Array.Empty<ImDrawVert>();
	public ushort[] Indices { get; init; } = Array.Empty<ushort>();

	private Action<UiFrameData>? _releaseAction;
	private Action<ImGuiFontAtlas>? _fontAtlasUploadedAction;

	internal void SetRelease(Action<UiFrameData>? releaseAction)
	{
		_releaseAction = releaseAction;
	}

	internal void SetFontAtlasUploaded(Action<ImGuiFontAtlas>? fontAtlasUploadedAction)
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
		var releaseAction = _releaseAction;
		_releaseAction = null;
		_fontAtlasUploadedAction = null;
		releaseAction?.Invoke(this);
	}
}

public sealed class ImGuiFontAtlas
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
