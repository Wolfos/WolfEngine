using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Runtime;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class GraphicsSettingsTests
{
	[Test]
	public void SupportedResolutionsKeepTheDisplayAspectAndEndAtNative()
	{
		var native = new Int2(2560, 1600);

		var resolutions = DisplayResolutions.ForNativeResolution(native);

		Assert.That(resolutions[^1], Is.EqualTo(native));
		Assert.That(resolutions, Does.Contain(new Int2(1152, 720)));
		Assert.That(resolutions, Does.Contain(new Int2(1920, 1200)));
		foreach (var resolution in resolutions)
		{
			Assert.That(resolution.X, Is.LessThanOrEqualTo(native.X));
			Assert.That(resolution.Y, Is.LessThanOrEqualTo(native.Y));
		}

		var heights = resolutions.Select(resolution => resolution.Y).ToArray();
		Assert.That(heights, Is.Ordered.Ascending);
		Assert.That(heights.Distinct().Count(), Is.EqualTo(heights.Length));
	}

	[Test]
	public void SupportedResolutionsAreEmptyForAnUnknownDisplay()
	{
		Assert.That(DisplayResolutions.ForNativeResolution(Int2.Zero), Is.Empty);
	}

	[TestCase(0, 0, 3840, 2160)]
	[TestCase(1920, 1080, 1920, 1080)]
	[TestCase(5120, 2880, 3840, 2160)]
	[TestCase(1920, 0, 3840, 2160)]
	public void FullscreenResolutionIsNativeByDefaultAndNeverLarger(int width, int height, int expectedWidth, int expectedHeight)
	{
		var resolved = DisplayResolutions.ResolveFullscreen(new Int2(width, height), new Int2(3840, 2160));

		Assert.That(resolved, Is.EqualTo(new Int2(expectedWidth, expectedHeight)));
	}

	[Test]
	public void GameOwnedWindowForwardsModeResolutionAndVSync()
	{
		var renderer = new RecordingRenderer();
		var service = new GraphicsSettingsService(
			renderer,
			new RenderPresentationOptions { OutputMode = RenderOutputMode.FullWindow });
		var settings = new GraphicsSettings
		{
			WindowMode = WindowMode.Fullscreen,
			Resolution = new Int2(1920, 1080),
			VSync = false
		};

		service.Apply(settings);

		Assert.That(service.CanChangeWindow, Is.True);
		Assert.That(service.Current, Is.EqualTo(settings));
		Assert.That(renderer.VSyncEnabled, Is.False);
		Assert.That(
			renderer.Requests,
			Is.EqualTo(new[] { new WindowConfiguration(WindowMode.Fullscreen, new Int2(1920, 1080)) }));
	}

	[Test]
	public void EditorViewportRecordsWindowSettingsWithoutChangingTheWindow()
	{
		var renderer = new RecordingRenderer();
		var service = new GraphicsSettingsService(renderer, new RenderPresentationOptions());

		service.Apply(service.Current with
		{
			WindowMode = WindowMode.Fullscreen,
			VSync = false
		});

		Assert.That(service.CanChangeWindow, Is.False);
		Assert.That(service.Current.WindowMode, Is.EqualTo(WindowMode.Fullscreen));
		Assert.That(renderer.VSyncEnabled, Is.False);
		Assert.That(renderer.Requests, Is.Empty);
	}

	[Test]
	public void NegativeResolutionIsRejected()
	{
		var service = new GraphicsSettingsService(
			new RecordingRenderer(),
			new RenderPresentationOptions { OutputMode = RenderOutputMode.FullWindow });

		Assert.Throws<ArgumentOutOfRangeException>(() => service.Apply(new GraphicsSettings { Resolution = new Int2(-1, 720) }));
	}

	[Test]
	public void SettingsWrittenBeforeWindowModeExistedStartFullscreen()
	{
		var settings = JsonSerializer.Deserialize(
			"""{"Title":"Game","Width":1600,"Height":900}"""u8,
			EngineJsonContext.Default.CookedRuntimeSettings)!;

		var graphics = RuntimeHost.CreateInitialGraphicsSettings(settings);

		Assert.That(graphics.WindowMode, Is.EqualTo(WindowMode.Fullscreen));
		Assert.That(graphics.Resolution, Is.EqualTo(Int2.Zero));
	}

	[Test]
	public void WindowedProjectStartsAtItsWindowSize()
	{
		var settings = new CookedRuntimeSettings
		{
			WindowMode = WindowMode.Windowed,
			Width = 1600,
			Height = 900
		};

		var graphics = RuntimeHost.CreateInitialGraphicsSettings(settings);

		Assert.That(graphics.WindowMode, Is.EqualTo(WindowMode.Windowed));
		Assert.That(graphics.Resolution, Is.EqualTo(new Int2(1600, 900)));
	}

	[Test]
	public void WindowModeRoundTripsThroughCookedSettings()
	{
		var bytes = JsonSerializer.SerializeToUtf8Bytes(
			new CookedRuntimeSettings { WindowMode = WindowMode.Windowed },
			EngineJsonContext.Default.CookedRuntimeSettings);

		var settings = JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.CookedRuntimeSettings)!;

		Assert.That(settings.WindowMode, Is.EqualTo(WindowMode.Windowed));
	}

	private sealed class RecordingRenderer : IRenderer
	{
		public List<WindowConfiguration> Requests { get; } = [];

		public bool VSyncEnabled { get; set; } = true;

		public bool CanConfigureWindow => true;

		public void RequestWindowConfiguration(WindowConfiguration configuration)
		{
			Requests.Add(configuration);
		}

		public void Run(Action startup, Action<float> update, Action<float> render) => throw new NotSupportedException();
		public IMaterialResources CreateMaterialResources(Material material) => throw new NotSupportedException();
		public ITextureResources CreateTextureResources(Texture texture) => throw new NotSupportedException();
		public IGfxDevice GetGfxDevice() => throw new NotSupportedException();
		public Int2 GetFrameBufferSize() => throw new NotSupportedException();
		public Int2 GetWindowSize() => throw new NotSupportedException();
		public void BeginFrame() => throw new NotSupportedException();
		public void Render(RenderGraphResourceRegistry resourceRegistry, RenderGraphResourceHandle finalColor) => throw new NotSupportedException();
		public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height) => throw new NotSupportedException();
		public void EnsureMeshResources(Mesh mesh) => throw new NotSupportedException();
		public void ReleaseMeshResources(Mesh mesh) => throw new NotSupportedException();
		public IGfxBuffer GetPackedMeshVertexBuffer() => throw new NotSupportedException();
		public IGfxBuffer GetPackedMeshIndexBuffer() => throw new NotSupportedException();
		public ulong PackedGeometryGeneration => 0;
		public uint GetPackedMeshVertexStride() => throw new NotSupportedException();
		public bool SupportsGpuCapture => false;
		public bool IsGpuCaptureActive => false;
		public string LastGpuCapturePath => string.Empty;

		public bool TryStartGpuCapture(string outputPath, out string error)
		{
			error = string.Empty;

			return false;
		}

		public bool TryStopGpuCapture(out string error)
		{
			error = string.Empty;

			return false;
		}
	}
}
