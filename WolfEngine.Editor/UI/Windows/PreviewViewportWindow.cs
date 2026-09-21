using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>Displays the editor's independent preview world as a second scene viewport.</summary>
public sealed class PreviewViewportWindow : EditorWindow
{
	private readonly IRenderViewHost _viewHost;
	private readonly IWorldManager _worldManager;
	private readonly EditorRenderViews _renderViews;
	private readonly EditorViewportStateBus _viewportStateBus;
	private EditorPreviewScene? _preview;

	public PreviewViewportWindow(
		IRenderViewHost viewHost,
		IWorldManager worldManager,
		EditorRenderViews renderViews,
		EditorViewportStateBus viewportStateBus)
	{
		_viewHost = viewHost ?? throw new ArgumentNullException(nameof(viewHost));
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
		_renderViews = renderViews ?? throw new ArgumentNullException(nameof(renderViews));
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
	}

	public override string Name => "Preview";

	public override void Draw(EditorScene scene)
	{
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
		ImGui.SetNextWindowSize(new Vector2(480.0f, 360.0f), ImGuiCond.FirstUseEver);
		Begin();
		ImGui.PopStyleVar();

		// Creating the view at registration time would make a hidden preview render on editor startup.
		var preview = _preview ??= new EditorPreviewScene(_viewHost, _worldManager, _renderViews, Name);
		var io = ImGui.GetIO();
		var contentSize = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.Zero);
		var imageMin = ImGui.GetCursorScreenPos();
		var imageMax = imageMin + contentSize;
		var contentPixels = new Int2(
			Math.Max(0, (int)MathF.Round(contentSize.X * io.DisplayFramebufferScale.X)),
			Math.Max(0, (int)MathF.Round(contentSize.Y * io.DisplayFramebufferScale.Y)));
		var visible = !ImGui.IsWindowCollapsed() &&
		              (!ImGui.IsWindowDocked() || IsSelectedTab) &&
		              contentPixels.X > 0 && contentPixels.Y > 0;

		if (contentSize.X > 0.0f && contentSize.Y > 0.0f)
		{
			ImGui.Image(UiTextureIds.Viewport(preview.View), contentSize);
		}

		_viewportStateBus.PublishUiState(preview.View, new SceneViewportUiState(
			visible,
			contentPixels,
			1.0f,
			SceneDebugViewIds.FinalColor,
			ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem),
			ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows),
			pointerAvailable: false,
			pointerCaptured: false,
			rightMousePressStartedHere: false,
			imageMin,
			imageMax));

		ImGui.End();
	}

	public override void OnHidden()
	{
		if (_preview is { } preview)
		{
			_viewportStateBus.PublishUiState(preview.View, SceneViewportUiState.Hidden);
		}
	}
}
