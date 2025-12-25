using System.Collections.Concurrent;
using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.UI;

public interface IImGuiInputSink
{
	void SetKey(ImGuiKey key, bool down);
	void AddChar(char c);
	void SetMousePosition(Vector2 position);
	void SetMouseButton(int button, bool down);
	void AddMouseScroll(Vector2 scroll);
}

public interface IUiFrameProvider
{
	bool TryConsumeLatest(out UiFrameData frame);
	public void NewFrame(float deltaTime, Int2 windowSize, Int2 framebufferSize);
	public void RunGui(Action<World> draw, World world);

}

/// <summary>
/// Game-thread owner of ImGui context; produces UiFrameData snapshots for the render thread.
/// </summary>
public unsafe sealed class ImGuiUiSystem : IImGuiInputSink, IUiFrameProvider
{
	private readonly ConcurrentQueue<UiFrameData> _pendingFrames = new();
	private readonly IntPtr _context;
	private readonly bool[] _mouseButtons = new bool[5];
	private Vector2 _mousePosition = new(-1, -1);
	private Vector2 _mouseWheel = Vector2.Zero;
	private readonly ImGuiFontAtlas _fontAtlas;

	public ImGuiUiSystem()
	{
		_context = ImGui.CreateContext();
		ImGui.SetCurrentContext(_context);
		var io = ImGui.GetIO();
		io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
		io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
		io.Fonts.Clear();
		io.Fonts.AddFontFromFileTTF("Assets/Fonts/Inter-VariableFont_opsz,wght.ttf", 14.0f);
		ApplyStyle();
		_fontAtlas = BuildFontAtlas(io);
	}

	public void NewFrame(float deltaTime, Int2 windowSize, Int2 framebufferSize)
	{
		ImGui.SetCurrentContext(_context);
		var io = ImGui.GetIO();
		io.DisplaySize = new Vector2(windowSize.X, windowSize.Y);
		io.DeltaTime = Math.Max(deltaTime, 1e-6f);
		if (windowSize.X > 0 && windowSize.Y > 0)
		{
			io.DisplayFramebufferScale = new Vector2(
				framebufferSize.X / (float)windowSize.X,
				framebufferSize.Y / (float)windowSize.Y);
		}
		else
		{
			io.DisplayFramebufferScale = Vector2.One;
		}

		for (var i = 0; i < _mouseButtons.Length; i++)
		{
			io.MouseDown[i] = _mouseButtons[i];
		}

		io.MousePos = _mousePosition;
		io.MouseWheel = _mouseWheel.Y;
		io.MouseWheelH = _mouseWheel.X;
		_mouseWheel = Vector2.Zero;

		ImGui.NewFrame();
	}

	public void RunGui(Action<World> draw, World world)
	{
		ImGui.SetCurrentContext(_context);
		draw(world);
		ImGui.Render();
		CaptureFrame();
	}

	private void CaptureFrame()
	{
		var drawData = ImGui.GetDrawData();
		if (drawData.CmdListsCount == 0)
		{
			return;
		}

		var totalVtx = drawData.TotalVtxCount;
		var totalIdx = drawData.TotalIdxCount;

		var verts = new ImDrawVert[totalVtx];
		var indices = new ushort[totalIdx];
		var commands = new List<UiDrawCommand>(drawData.CmdListsCount * 4);

		var vtxOffset = 0;
		var idxOffset = 0;

		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var list = drawData.CmdLists[n];

			for (var v = 0; v < list.VtxBuffer.Size; v++)
			{
				var src = list.VtxBuffer[v];
				unsafe
				{
					var ptr = (ImDrawVert*) src.NativePtr;
					verts[vtxOffset + v] = *ptr;
				}
			}

			for (var i = 0; i < list.IdxBuffer.Size; i++)
			{
				indices[idxOffset + i] = list.IdxBuffer[i];
			}

			for (var c = 0; c < list.CmdBuffer.Size; c++)
			{
				var cmd = list.CmdBuffer[c];
				var clip = cmd.ClipRect;
				var cmdEntry = new UiDrawCommand(
					(int) cmd.ElemCount,
					idxOffset + (int) cmd.IdxOffset,
					vtxOffset + (int) cmd.VtxOffset,
					new Vector4(clip.X, clip.Y, clip.Z, clip.W));
				commands.Add(cmdEntry);
			}

			vtxOffset += list.VtxBuffer.Size;
			idxOffset += list.IdxBuffer.Size;
		}

		var io = ImGui.GetIO();
		var framebufferSize = new Vector2(
			io.DisplaySize.X * io.DisplayFramebufferScale.X,
			io.DisplaySize.Y * io.DisplayFramebufferScale.Y);

		var frame = new UiFrameData
		{
			VertexCount = totalVtx,
			IndexCount = totalIdx,
			DisplayPos = drawData.DisplayPos,
			DisplaySize = drawData.DisplaySize,
			FramebufferSize = framebufferSize,
			DeltaTime = ImGui.GetIO().DeltaTime,
			Vertices = verts,
			Indices = indices,
			Commands = commands.ToArray(),
			HasFontAtlas = _fontAtlas.PixelsRgba.Length > 0,
			FontAtlas = _fontAtlas
		};

		_pendingFrames.Enqueue(frame);
		while (_pendingFrames.Count > 2 && _pendingFrames.TryDequeue(out _))
		{
		}
	}

	public bool TryConsumeLatest(out UiFrameData frame)
	{
		frame = UiFrameData.Empty;
		while (_pendingFrames.TryDequeue(out var candidate))
		{
			frame = candidate;
		}

		return frame != UiFrameData.Empty && frame.VertexCount + frame.IndexCount > 0;
	}

	public void SetKey(ImGuiKey key, bool down)
	{
		ImGui.SetCurrentContext(_context);
		ImGui.GetIO().AddKeyEvent(key, down);
	}

	public void AddChar(char c)
	{
		ImGui.SetCurrentContext(_context);
		ImGui.GetIO().AddInputCharacter(c);
	}

	public void SetMousePosition(Vector2 position)
	{
		_mousePosition = position;
	}

	public void SetMouseButton(int button, bool down)
	{
		if (button >= 0 && button < _mouseButtons.Length)
		{
			_mouseButtons[button] = down;
		}
	}

	public void AddMouseScroll(Vector2 scroll)
	{
		_mouseWheel += scroll;
	}

	private static ImGuiFontAtlas BuildFontAtlas(ImGuiIOPtr io)
	{
		io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out _);
		if (pixels == null || width == 0 || height == 0)
		{
			return new ImGuiFontAtlas();
		}

		var size = width * height * 4;
		var fontPixels = new byte[size];
		for (var i = 0; i < size; i++)
		{
			fontPixels[i] = pixels[i];
		}

		return new ImGuiFontAtlas
		{
			Width = width,
			Height = height,
			PixelsRgba = fontPixels
		};
	}

	private static void ApplyStyle()
	{
		var style = ImGui.GetStyle();
		var textColor = new Vector4(0.93333334f, 0.93333334f, 0.93333334f, 1.0f);
		var bgColor = new Vector4(0.20784314f, 0.21176471f, 0.23137255f, 1.0f);
		var titleColor = new Vector4(0.11764706f, 0.12156863f, 0.13725491f, 1.0f);
		var buttonColor = new Vector4(0.14117648f, 0.14509805f, 0.16078432f, 1.0f);
		
		style.Colors[(int)ImGuiCol.Text] = textColor;
		style.Colors[(int)ImGuiCol.WindowBg] = bgColor;
		style.Colors[(int)ImGuiCol.PopupBg] = bgColor;
		style.Colors[(int)ImGuiCol.TitleBg] = titleColor;
		style.Colors[(int)ImGuiCol.TitleBgCollapsed] = titleColor;
		style.Colors[(int)ImGuiCol.TitleBgActive] = titleColor;
		style.Colors[(int)ImGuiCol.Button] = buttonColor;
		style.Colors[(int)ImGuiCol.FrameBg] = buttonColor;
		style.Colors[(int)ImGuiCol.Header] = bgColor;
		style.Colors[(int)ImGuiCol.Border] = bgColor;
		
		style.Colors[(int)ImGuiCol.Tab] = bgColor;
		style.Colors[(int)ImGuiCol.TabDimmed] = bgColor;
		style.Colors[(int)ImGuiCol.TabSelected] = bgColor;
		style.Colors[(int)ImGuiCol.TabDimmedSelected] = bgColor;
		style.Colors[(int)ImGuiCol.TabDimmedSelectedOverline] = bgColor;
		style.Colors[(int)ImGuiCol.TabHovered] = bgColor;
		style.Colors[(int)ImGuiCol.TabSelectedOverline] = bgColor;
	}
}
