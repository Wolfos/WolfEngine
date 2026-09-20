using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RenderViewTests
{
	[Test]
	public void UiTextureIds_PrimaryViewportSentinel_IsTheLegacySceneViewportId()
	{
		// The editor draws its scene image with UiTextureIds.SceneViewport. That id has to keep meaning
		// the primary view, or every existing viewport draw resolves to the wrong texture.
		Assert.That(UiTextureIds.Viewport(RenderViewId.Primary), Is.EqualTo(UiTextureIds.SceneViewport));
	}

	[Test]
	public void UiTextureIds_ViewportSentinels_RoundTripAndAreDistinctPerView()
	{
		var seen = new HashSet<nint>();
		for (var index = 0; index < UiTextureIds.MaxViewports; index++)
		{
			var view = RenderViewId.FromIndex(index);
			var sentinel = UiTextureIds.Viewport(view);

			Assert.That(seen.Add(sentinel), Is.True, $"sentinel for {view} collided with another view's");
			Assert.That(UiTextureIds.TryGetViewport(sentinel, out var resolved), Is.True);
			Assert.That(resolved, Is.EqualTo(view));
		}
	}

	[Test]
	public void UiTextureIds_TryGetViewport_RejectsEverythingOutsideTheReservedBlock()
	{
		// Bindless handles are packed into the positive range and the font atlas sits just above the block.
		// Anything that is not a viewport sentinel has to be left alone, or a real texture gets rewritten.
		Assert.Multiple(() =>
		{
			Assert.That(UiTextureIds.IsViewport(0), Is.False);
			Assert.That(UiTextureIds.IsViewport(1), Is.False);
			Assert.That(UiTextureIds.IsViewport(4096), Is.False);
			Assert.That(UiTextureIds.IsViewport(UiTextureIds.FontAtlas), Is.False);
			Assert.That(
				UiTextureIds.IsViewport(UiTextureIds.Viewport(RenderViewId.FromIndex(UiTextureIds.MaxViewports - 1)) - 1),
				Is.False,
				"an id one past the reserved block must not resolve to a view");
		});
	}

	[Test]
	public void UiTextureIds_Viewport_RejectsViewsOutsideTheBlock()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => UiTextureIds.Viewport(RenderViewId.FromIndex(UiTextureIds.MaxViewports)));
		Assert.Throws<ArgumentException>(() => UiTextureIds.Viewport(RenderViewId.None));
	}

	[Test]
	public void RenderViewId_NoneAndPrimary_AreDistinctAndOnlyPrimaryIsValid()
	{
		Assert.Multiple(() =>
		{
			Assert.That(RenderViewId.None.IsValid, Is.False);
			Assert.That(RenderViewId.Primary.IsValid, Is.True);
			Assert.That(RenderViewId.Primary, Is.Not.EqualTo(RenderViewId.None));
			Assert.That(RenderViewId.FromIndex(0), Is.EqualTo(RenderViewId.Primary));
		});
	}

	[Test]
	public void EditorViewportStateBus_UiState_IsHeldPerView()
	{
		var bus = new EditorViewportStateBus();
		var second = RenderViewId.FromIndex(1);

		bus.PublishUiState(RenderViewId.Primary, CreateUiState(new Int2(800, 600), hovered: true));
		bus.PublishUiState(second, CreateUiState(new Int2(320, 240), hovered: false));

		Assert.Multiple(() =>
		{
			Assert.That(bus.GetUiState(RenderViewId.Primary).ContentSizePixels, Is.EqualTo(new Int2(800, 600)));
			Assert.That(bus.GetUiState(RenderViewId.Primary).Hovered, Is.True);
			Assert.That(bus.GetUiState(second).ContentSizePixels, Is.EqualTo(new Int2(320, 240)));
			Assert.That(bus.GetUiState(second).Hovered, Is.False);
		});
	}

	[Test]
	public void EditorViewportStateBus_ParameterlessMembers_AddressThePrimaryView()
	{
		var bus = new EditorViewportStateBus();
		var second = RenderViewId.FromIndex(1);
		bus.PublishUiState(CreateUiState(new Int2(1280, 720), hovered: true));
		bus.PublishRenderState(new SceneViewportRenderState(
			textureId: 42,
			renderSizePixels: new Int2(1280, 720),
			projection: System.Numerics.Matrix4x4.Identity,
			debugViews: [],
			activeDebugViewId: SceneDebugViewIds.FinalColor));

		Assert.Multiple(() =>
		{
			Assert.That(bus.GetUiState().ContentSizePixels, Is.EqualTo(new Int2(1280, 720)));
			Assert.That(bus.GetUiState(RenderViewId.Primary).ContentSizePixels, Is.EqualTo(new Int2(1280, 720)));
			Assert.That(bus.GetRenderState().TextureId, Is.EqualTo((nint)42));
			Assert.That(bus.GetRenderState(RenderViewId.Primary).TextureId, Is.EqualTo((nint)42));
			// A view nobody published to reads as hidden rather than inheriting the primary's state.
			Assert.That(bus.GetUiState(second).Visible, Is.False);
			Assert.That(bus.GetRenderState(second).TextureId, Is.EqualTo((nint)0));
		});
	}

	[Test]
	public void EditorViewportStateBus_RemoveView_DropsStateSoASlotIsNotInherited()
	{
		var bus = new EditorViewportStateBus();
		var second = RenderViewId.FromIndex(1);
		bus.PublishUiState(second, CreateUiState(new Int2(320, 240), hovered: true));
		Assert.That(bus.GetViews(), Does.Contain(second));

		bus.RemoveView(second);

		Assert.Multiple(() =>
		{
			Assert.That(bus.GetViews(), Does.Not.Contain(second));
			Assert.That(bus.GetUiState(second).Visible, Is.False);
			Assert.That(bus.GetUiState(second).Hovered, Is.False);
		});
	}

	[Test]
	public void EditorViewportStateBus_OverrideDebugView_AppliesToEveryViewIncludingLaterPublishes()
	{
		var bus = new EditorViewportStateBus();
		var second = RenderViewId.FromIndex(1);
		bus.PublishUiState(RenderViewId.Primary, CreateUiState(new Int2(800, 600), hovered: true));

		bus.OverrideDebugView(SceneDebugViewIds.GBufferNormal);
		bus.PublishUiState(second, CreateUiState(new Int2(320, 240), hovered: false));

		Assert.Multiple(() =>
		{
			Assert.That(
				bus.GetUiState(RenderViewId.Primary).RequestedDebugViewId,
				Is.EqualTo(SceneDebugViewIds.GBufferNormal),
				"an override has to reach views that already published");
			Assert.That(
				bus.GetUiState(second).RequestedDebugViewId,
				Is.EqualTo(SceneDebugViewIds.GBufferNormal),
				"an override has to reach views that publish after it");
		});
	}

	[Test]
	public void Camera_GetPerspective_DerivesAspectFromTheViewSizeNotTheComponent()
	{
		// The point of the change: one camera rendered by two views at different sizes must produce two
		// projections. Camera.Perspective can only hold one, so views cannot read it.
		var camera = new Camera { Fov = 70.0f, NearPlane = 0.1f, FarPlane = 1000.0f };
		camera.ScreenResolution = new Int2(1280, 720);
		camera.SetPerspective(70.0f);

		var wide = camera.GetPerspective(new Int2(1600, 400));
		var tall = camera.GetPerspective(new Int2(400, 1600));

		Assert.Multiple(() =>
		{
			Assert.That(wide.M11, Is.Not.EqualTo(tall.M11).Within(0.0001f));
			// A 16:9 request has to reproduce what the component baked for 1280x720.
			Assert.That(camera.GetPerspective(new Int2(1280, 720)).M11, Is.EqualTo(camera.Perspective.M11).Within(0.0001f));
			// The component is left alone; resolving a projection is not a mutation.
			Assert.That(camera.ScreenResolution, Is.EqualTo(new Int2(1280, 720)));
		});
	}

	[Test]
	public void Camera_GetPerspective_ToleratesDegenerateSizes()
	{
		var camera = new Camera { Fov = 70.0f };
		Assert.DoesNotThrow(() => camera.GetPerspective(Int2.Zero));
		Assert.That(camera.GetPerspective(Int2.Zero).M11, Is.EqualTo(camera.GetPerspective(new Int2(1, 1)).M11).Within(0.0001f));
	}

	private static SceneViewportUiState CreateUiState(Int2 contentSizePixels, bool hovered) => new(
		visible: true,
		contentSizePixels: contentSizePixels,
		resolutionScale: 1.0f,
		requestedDebugViewId: SceneDebugViewIds.FinalColor,
		hovered: hovered,
		focused: false,
		pointerAvailable: false,
		pointerCaptured: false,
		rightMousePressStartedHere: false,
		imageMin: System.Numerics.Vector2.Zero,
		imageMax: System.Numerics.Vector2.Zero);
}
