using System;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.UI;

/// <summary>Game-thread UI snapshots consumed by the render graph. Implementations use a latest-wins queue.</summary>
public interface IGameplayUiFrameProvider
{
	/// <summary>
	/// Reports the render target the screen UI is drawn into.
	/// </summary>
	/// <param name="size">Target size in physical pixels.</param>
	/// <param name="displayScale">
	/// Physical pixels per logical pixel, i.e. framebuffer size over window size. On a HiDPI display this is
	/// greater than one, and the UI lays itself out in logical pixels so that authored sizes stay the same
	/// apparent size on every display. Mirrors what <c>ImGuiUiSystem</c> does with DisplayFramebufferScale.
	/// </param>
	void SetViewportSize(Int2 size, float displayScale = 1.0f);
	bool TryConsumeLatest(out GameplayUiRenderFrame frame);
}

public sealed class NullGameplayUiFrameProvider : IGameplayUiFrameProvider
{
	public static NullGameplayUiFrameProvider Instance { get; } = new();
	private NullGameplayUiFrameProvider() { }
	public void SetViewportSize(Int2 size, float displayScale = 1.0f) { }
	public bool TryConsumeLatest(out GameplayUiRenderFrame frame)
	{
		frame = GameplayUiRenderFrame.Empty;
		return false;
	}
}

public sealed class GameplayUiRenderFrame
{
	public static GameplayUiRenderFrame Empty { get; } = new();
	public UiFrameData Screen { get; init; } = UiFrameData.Empty;
	public GameplayUiTextureSurfaceFrame[] TextureSurfaces { get; init; } = Array.Empty<GameplayUiTextureSurfaceFrame>();

	public void Release()
	{
		Screen.Release();
		for (var i = 0; i < TextureSurfaces.Length; i++)
		{
			TextureSurfaces[i].Frame.Release();
		}
	}
}

public sealed class GameplayUiTextureSurfaceFrame
{
	public required long SurfaceId { get; init; }
	public required Texture Target { get; init; }
	public UiFrameData Frame { get; init; } = UiFrameData.Empty;
	public bool IsDirty { get; set; }
	public ColorRGBA ClearColor { get; init; } = new(0.0f, 0.0f, 0.0f, 0.0f);
}
