using System;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
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
	public void RunGui(Action draw);

}

/// <summary>
/// Game-thread owner of ImGui context; produces UiFrameData snapshots for the render thread.
/// </summary>
public sealed unsafe class ImGuiUiSystem : IImGuiInputSink, IUiFrameProvider
{
	private static readonly string FontPath = Path.Combine(
		AppContext.BaseDirectory,
		"Assets",
		"Fonts",
		"Inter-VariableFont_opsz,wght.ttf");
	private const float BaseFontSize = 15.0f;
	private const float FontScaleEpsilon = 0.01f;
	private readonly ConcurrentQueue<UiFrameData> _pendingFrames = new();
	private readonly ConcurrentQueue<(ImGuiKey key, bool down)> _pendingKeys = new();
	private readonly ConcurrentQueue<char> _pendingChars = new();
	private readonly IntPtr _context;
	private readonly object _contextLock = new();
	private readonly bool[] _mouseButtons = new bool[5];
	private bool _leftShiftDown;
	private bool _rightShiftDown;
	private bool _leftCtrlDown;
	private bool _rightCtrlDown;
	private bool _leftSuperDown;
	private bool _rightSuperDown;
	private Vector2 _mousePosition = new(-1, -1);
	private Vector2 _mouseWheel = Vector2.Zero;
	private ImGuiFontAtlas _fontAtlas;
	private float _fontDpiScale = 1.0f;
	private bool _fontAtlasDirty = true;
	private static ImFontPtr _regularFont;
	private static ImFontPtr _boldFont;

	public ImGuiUiSystem()
	{
		_context = ImGui.CreateContext();
		ImGui.SetCurrentContext(_context);
		var io = ImGui.GetIO();
		io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
		io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
		ApplyDefaultStyle();

		RebuildFonts(1.0f);
	}

	public void NewFrame(float deltaTime, Int2 windowSize, Int2 framebufferSize)
	{
		lock (_contextLock)
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
			var dpiScale = (io.DisplayFramebufferScale.X + io.DisplayFramebufferScale.Y) * 0.5f;
			if (dpiScale > 0.0f && Math.Abs(dpiScale - _fontDpiScale) > FontScaleEpsilon)
			{
				RebuildFonts(dpiScale);
			}

			for (var i = 0; i < _mouseButtons.Length; i++)
			{
				io.MouseDown[i] = _mouseButtons[i];
			}

			while (_pendingKeys.TryDequeue(out var keyEvent))
			{
				io.AddKeyEvent(keyEvent.key, keyEvent.down);
				UpdateModifierState(io, keyEvent.key, keyEvent.down);
			}

			while (_pendingChars.TryDequeue(out var character))
			{
				io.AddInputCharacter(character);
			}

			io.MousePos = _mousePosition;
			io.MouseWheel = _mouseWheel.Y;
			io.MouseWheelH = _mouseWheel.X;
			_mouseWheel = Vector2.Zero;

			ImGui.NewFrame();
		}
	}

	private void UpdateModifierState(ImGuiIOPtr io, ImGuiKey key, bool down)
	{
		switch (key)
		{
			case ImGuiKey.LeftShift: _leftShiftDown = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShiftDown || _rightShiftDown); break;
			case ImGuiKey.RightShift: _rightShiftDown = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShiftDown || _rightShiftDown); break;
			case ImGuiKey.LeftCtrl: _leftCtrlDown = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrlDown || _rightCtrlDown); break;
			case ImGuiKey.RightCtrl: _rightCtrlDown = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrlDown || _rightCtrlDown); break;
			case ImGuiKey.LeftSuper: _leftSuperDown = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuperDown || _rightSuperDown); break;
			case ImGuiKey.RightSuper: _rightSuperDown = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuperDown || _rightSuperDown); break;
		}
	}

	public void RunGui(Action draw)
	{
		lock (_contextLock)
		{
			ImGui.SetCurrentContext(_context);
			draw();
			ImGui.Render();
			CaptureFrame();
		}
	}

	public static bool PushBoldFont()
	{
		var font = _boldFont.NativePtr != null ? _boldFont : _regularFont;
		if (font.NativePtr == null)
		{
			return false;
		}

		ImGui.PushFont(font);
		return true;
	}

	public static bool PushRegularFont()
	{
		if (_regularFont.NativePtr == null)
		{
			return false;
		}

		ImGui.PushFont(_regularFont);
		return true;
	}

	public static void PopFontIfPushed(bool pushed)
	{
		if (pushed)
		{
			ImGui.PopFont();
		}
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
		var totalCmd = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			totalCmd += drawData.CmdLists[n].CmdBuffer.Size;
		}

		var verts = ArrayPool<ImDrawVert>.Shared.Rent(Math.Max(totalVtx, 1));
		var indices = ArrayPool<ushort>.Shared.Rent(Math.Max(totalIdx, 1));
		var commands = ArrayPool<UiDrawCommand>.Shared.Rent(Math.Max(totalCmd, 1));

		var vtxOffset = 0;
		var idxOffset = 0;
		var cmdOffset = 0;

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
					commands[cmdOffset++] = new UiDrawCommand(
						(int) cmd.ElemCount,
						idxOffset + (int) cmd.IdxOffset,
						vtxOffset + (int) cmd.VtxOffset,
						new Vector4(clip.X, clip.Y, clip.Z, clip.W),
						cmd.TextureId);
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
			CommandCount = cmdOffset,
			DisplayPos = drawData.DisplayPos,
			DisplaySize = drawData.DisplaySize,
			FramebufferSize = framebufferSize,
			DeltaTime = ImGui.GetIO().DeltaTime,
			Vertices = verts,
			Indices = indices,
			Commands = commands,
			HasFontAtlas = _fontAtlasDirty && _fontAtlas.PixelsRgba.Length > 0,
			FontAtlas = _fontAtlas
		};
		frame.SetRelease(ReturnPooledFrame);

		_pendingFrames.Enqueue(frame);
		while (_pendingFrames.Count > 2 && _pendingFrames.TryDequeue(out var dropped))
		{
			dropped.Release();
		}
	}

	public bool TryConsumeLatest(out UiFrameData frame)
	{
		frame = UiFrameData.Empty;
		while (_pendingFrames.TryDequeue(out var candidate))
		{
			if (ReferenceEquals(frame, UiFrameData.Empty) == false)
			{
				frame.Release();
			}
			frame = candidate;
		}

		if (ReferenceEquals(frame, UiFrameData.Empty))
		{
			return false;
		}

		if (frame.HasFontAtlas)
		{
			_fontAtlasDirty = false;
		}

		if (frame.VertexCount + frame.IndexCount == 0)
		{
			frame.Release();
			frame = UiFrameData.Empty;
			return false;
		}

		return true;
	}

	public void SetKey(ImGuiKey key, bool down)
	{
		_pendingKeys.Enqueue((key, down));
	}

	public void AddChar(char c)
	{
		_pendingChars.Enqueue(c);
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

	private void RebuildFonts(float dpiScale)
	{
		ImGui.SetCurrentContext(_context);
		var io = ImGui.GetIO();
		_fontDpiScale = dpiScale;
		io.FontGlobalScale = dpiScale > 0.0f ? 1.0f / dpiScale : 1.0f;
		io.Fonts.Clear();
		_regularFont = default;
		_boldFont = default;
		_regularFont = io.Fonts.AddFontFromFileTTF(FontPath, BaseFontSize * dpiScale);
		_boldFont = TryLoadBoldFont(io, dpiScale);

		if (_boldFont.NativePtr == null)
		{
			var syntheticBoldConfig = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
			try
			{
				// Synthetic fallback when no dedicated bold face is available.
				syntheticBoldConfig.RasterizerMultiply = 1.55f;
				_boldFont = io.Fonts.AddFontFromFileTTF(FontPath, (BaseFontSize + 0.75f) * dpiScale, syntheticBoldConfig);
			}
			finally
			{
				syntheticBoldConfig.Destroy();
			}
		}

		if (_regularFont.NativePtr == null)
		{
			_regularFont = io.Fonts.AddFontDefault();
		}

		if (_boldFont.NativePtr == null)
		{
			_boldFont = _regularFont;
		}

		_fontAtlas = BuildFontAtlas(io);
		io.Fonts.SetTexID(UiTextureIds.FontAtlas);
		_fontAtlasDirty = true;
	}

	private static ImFontPtr TryLoadBoldFont(ImGuiIOPtr io, float dpiScale)
	{
		foreach (var candidatePath in EnumerateBoldFontPaths())
		{
			if (File.Exists(candidatePath) == false)
			{
				continue;
			}

			var boldFont = io.Fonts.AddFontFromFileTTF(candidatePath, BaseFontSize * dpiScale);
			if (boldFont.NativePtr != null)
			{
				return boldFont;
			}
		}

		return default;
	}

	private static IEnumerable<string> EnumerateBoldFontPaths()
	{
		yield return Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Inter-Bold.ttf");

		if (OperatingSystem.IsMacOS())
		{
			yield return "/System/Library/Fonts/Supplemental/Arial Bold.ttf";
		}
		else if (OperatingSystem.IsWindows())
		{
			yield return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.Windows),
				"Fonts",
				"arialbd.ttf");
		}
		else if (OperatingSystem.IsLinux())
		{
			yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
		}
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

	private static void ApplyDefaultStyle()
	{
		var style = ImGui.GetStyle();
		var textColor = new ColorRGBA(0.93333334f, 0.93333334f, 0.93333334f, 1.0f);
		var bgColor = new ColorRGBA(0.157f,0.165f,0.184f, 1.0f);
		var bgBright = new ColorRGBA(0.22f,0.224f,0.243f, 1.0f);
		var bgDark = new ColorRGBA(0.067f,0.075f,0.094f, 1.0f);
		var buttonColor = bgColor;
		var primary = new ColorRGBA(0.675f,0.78f,0.984f, 1.0f);
		var secondary = new ColorRGBA(0.745f,0.776f,0.855f, 1.0f);
		var secondaryContainer = new ColorRGBA(0.247f,0.275f,0.337f, 1.0f);
		var border = new ColorRGBA(0.00f, 0.00f, 0.00f, 0.35f);
		var separator    = new ColorRGBA(1.00f, 1.00f, 1.00f, 0.06f);
		

		style.FramePadding = new Vector2(5, 7);
		style.WindowPadding = new Vector2(10, 3);
		style.WindowBorderSize = 1;
		style.ChildBorderSize = 1;
		style.PopupBorderSize = 1;
		style.TabBorderSize = 0;
		style.TabBarBorderSize = 1;
		style.FrameBorderSize = 1;
		style.DockingSeparatorSize = 3;
		
		style.FrameRounding = 4.0f;
		style.ChildRounding = 4.0f;
		style.GrabRounding = 4.0f;
		style.PopupRounding = 4.0f;
		style.ScrollbarRounding = 4.0f;
		style.TabRounding = 6.0f;
		style.WindowRounding = 6.0f;
		style.WindowMenuButtonPosition = ImGuiDir.None;
		
		style.Colors[(int)ImGuiCol.Text] = textColor;
		style.Colors[(int)ImGuiCol.WindowBg] = bgColor;
		style.Colors[(int)ImGuiCol.ChildBg] = bgDark;
		style.Colors[(int)ImGuiCol.MenuBarBg] = bgDark;
		style.Colors[(int)ImGuiCol.PopupBg] = bgDark;
		style.Colors[(int)ImGuiCol.DockingEmptyBg] = bgDark;
		style.Colors[(int)ImGuiCol.TitleBg] = bgDark;
		style.Colors[(int)ImGuiCol.TitleBgCollapsed] = bgDark;
		style.Colors[(int)ImGuiCol.TitleBgActive] = bgDark;
		style.Colors[(int)ImGuiCol.Button] = buttonColor;
		style.Colors[(int)ImGuiCol.ButtonHovered] = bgBright;
		style.Colors[(int)ImGuiCol.ButtonActive] = secondaryContainer;
		style.Colors[(int)ImGuiCol.Header] = bgColor;
		style.Colors[(int)ImGuiCol.HeaderActive] = secondaryContainer;
		style.Colors[(int)ImGuiCol.HeaderHovered] = bgBright;
		style.Colors[(int)ImGuiCol.Border] = bgDark;
		style.Colors[(int)ImGuiCol.BorderShadow] = default(ColorRGBA);
		style.Colors[(int)ImGuiCol.Separator] = separator;
		style.Colors[(int)ImGuiCol.SeparatorHovered] = new ColorRGBA(primary.R, primary.G, primary.B, 0.25f);
		style.Colors[(int)ImGuiCol.SeparatorActive]  = new ColorRGBA(primary.R, primary.G, primary.B, 0.35f);
		
		style.Colors[(int)ImGuiCol.Tab] = bgDark;
		style.Colors[(int)ImGuiCol.TabDimmed] = bgDark;
		style.Colors[(int)ImGuiCol.TabSelected] = bgColor;
		style.Colors[(int)ImGuiCol.TabDimmedSelected] = bgColor;
		style.Colors[(int)ImGuiCol.TabDimmedSelectedOverline] = default(ColorRGBA);
		style.Colors[(int)ImGuiCol.TabHovered] = bgColor;
		style.Colors[(int)ImGuiCol.TabSelectedOverline] = default(ColorRGBA);
		
		style.Colors[(int)ImGuiCol.FrameBg] = secondaryContainer;
		style.Colors[(int)ImGuiCol.FrameBgHovered] = secondaryContainer;
		style.Colors[(int)ImGuiCol.FrameBgActive] = secondaryContainer;
		style.Colors[(int)ImGuiCol.CheckMark] = primary;
		style.Colors[(int)ImGuiCol.SliderGrab] = primary;
		style.Colors[(int)ImGuiCol.SliderGrabActive] = primary;
		style.Colors[(int)ImGuiCol.SliderGrabActive] = primary;
		style.Colors[(int)ImGuiCol.ResizeGrip] = primary;
		style.Colors[(int)ImGuiCol.ResizeGripActive] = primary;
		style.Colors[(int)ImGuiCol.ResizeGripHovered] = primary;
	}

	private static void ReturnPooledFrame(UiFrameData frame)
	{
		if (frame.Vertices.Length > 0)
		{
			ArrayPool<ImDrawVert>.Shared.Return(frame.Vertices, clearArray: false);
		}

		if (frame.Indices.Length > 0)
		{
			ArrayPool<ushort>.Shared.Return(frame.Indices, clearArray: false);
		}

		if (frame.Commands.Length > 0)
		{
			ArrayPool<UiDrawCommand>.Shared.Return(frame.Commands, clearArray: false);
		}
	}
}
