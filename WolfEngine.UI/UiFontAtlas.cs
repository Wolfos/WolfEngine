using System.Buffers;
using System.Numerics;
using ImGuiNET;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI;

internal sealed unsafe class UiFontAtlas : IDisposable
{
	private readonly object _sync = new();
	private readonly nint _context;
	private bool _disposed;

	public UiFontAtlas()
	{
		_context = ImGui.CreateContext();
		var previous = ImGui.GetCurrentContext();
		try
		{
			ImGui.SetCurrentContext(_context);
			var io = ImGui.GetIO();
			io.Fonts.AddFontDefault();
			io.Fonts.SetTexID(UiTextureIds.FontAtlas);
			io.Fonts.GetTexDataAsRGBA32(out byte* _, out _, out _, out _);
			// Geometry uses a deterministic vector/bitmap font, so the renderer only needs a white
			// texel. This also decouples gameplay UI from the native ImGui font-atlas ABI.
			Atlas = new ImGuiFontAtlas { Width = 1, Height = 1, PixelsRgba = [255, 255, 255, 255] };
		}
		finally
		{
			ImGui.SetCurrentContext(previous);
		}
	}

	public ImGuiFontAtlas Atlas { get; }

	public UiFrameData BuildFrame(int width, int height, Action<ImDrawListPtr> draw)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_sync)
		{
			var previous = ImGui.GetCurrentContext();
			try
			{
				ImGui.SetCurrentContext(_context);
				var io = ImGui.GetIO();
				io.DisplaySize = new Vector2(width, height);
				io.DisplayFramebufferScale = Vector2.One;
				io.DeltaTime = 1f / 60f;
				ImGui.NewFrame();
				draw(ImGui.GetBackgroundDrawList());
				ImGui.Render();
				return CaptureFrame(ImGui.GetDrawData(), width, height);
			}
			finally
			{
				ImGui.SetCurrentContext(previous);
			}
		}
	}

	private UiFrameData CaptureFrame(ImDrawDataPtr drawData, int width, int height)
	{
		var vertexCount = drawData.TotalVtxCount;
		var indexCount = drawData.TotalIdxCount;
		var vertices = ArrayPool<ImDrawVert>.Shared.Rent(Math.Max(vertexCount, 1));
		var indices = ArrayPool<ushort>.Shared.Rent(Math.Max(indexCount, 1));
		var commandCount = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++) commandCount += drawData.CmdLists[n].CmdBuffer.Size;
		var commands = ArrayPool<UiDrawCommand>.Shared.Rent(Math.Max(commandCount, 1));
		var vertexOffset = 0;
		var indexOffset = 0;
		var commandOffset = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var list = drawData.CmdLists[n];
			for (var i = 0; i < list.VtxBuffer.Size; i++)
				vertices[vertexOffset + i] = *(ImDrawVert*)list.VtxBuffer[i].NativePtr;
			for (var i = 0; i < list.IdxBuffer.Size; i++) indices[indexOffset + i] = list.IdxBuffer[i];
			for (var i = 0; i < list.CmdBuffer.Size; i++)
			{
				var command = list.CmdBuffer[i];
				commands[commandOffset++] = new UiDrawCommand(
					(int)command.ElemCount,
					indexOffset + (int)command.IdxOffset,
					vertexOffset + (int)command.VtxOffset,
					command.ClipRect,
					command.TextureId);
			}
			vertexOffset += list.VtxBuffer.Size;
			indexOffset += list.IdxBuffer.Size;
		}

		var frame = new UiFrameData
		{
			VertexCount = vertexCount,
			IndexCount = indexCount,
			CommandCount = commandCount,
			DisplayPos = drawData.DisplayPos,
			DisplaySize = drawData.DisplaySize,
			FramebufferSize = new Vector2(width, height),
			DeltaTime = 1f / 60f,
			Vertices = vertices,
			Indices = indices,
			Commands = commands,
			HasFontAtlas = true,
			FontAtlas = Atlas
		};
		frame.SetRelease(ReturnPooledFrame);
		return frame;
	}

	private static void ReturnPooledFrame(UiFrameData frame)
	{
		if (frame.Vertices.Length > 0) ArrayPool<ImDrawVert>.Shared.Return(frame.Vertices, clearArray: false);
		if (frame.Indices.Length > 0) ArrayPool<ushort>.Shared.Return(frame.Indices, clearArray: false);
		if (frame.Commands.Length > 0) ArrayPool<UiDrawCommand>.Shared.Return(frame.Commands, clearArray: false);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		lock (_sync)
		{
			var previous = ImGui.GetCurrentContext();
			ImGui.SetCurrentContext(_context);
			ImGui.DestroyContext(_context);
			ImGui.SetCurrentContext(previous == _context ? nint.Zero : previous);
		}
	}
}
