using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Moq;
using WolfEngine.AssetPipeline;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class VolumetricFogTests
{
	private const float Near = 0.1f;
	private const float MaxDistance = 250.0f;

	[Test]
	public void ConfigDefaultsAreDisabledWithDocumentedResolution()
	{
		var config = new VolumetricFogConfig();
		Assert.Multiple(() =>
		{
			Assert.That(config.Enabled, Is.False);
			Assert.That(config.FroxelPixelSize, Is.EqualTo(16));
			Assert.That(config.SliceCount, Is.EqualTo(64));
			Assert.That(config.HistoryWeight, Is.EqualTo(0.9f));
			Assert.That(new RenderConfig().VolumetricFog.Enabled, Is.False);
		});
	}

	[TestCase(1, 4)]
	[TestCase(4, 4)]
	[TestCase(24, 24)]
	[TestCase(64, 32)]
	public void FroxelPixelSizeIsClamped(int value, int expected)
	{
		Assert.That(new VolumetricFogConfig { FroxelPixelSize = value }.FroxelPixelSize, Is.EqualTo(expected));
	}

	[TestCase(1, 16)]
	[TestCase(48, 48)]
	[TestCase(512, 128)]
	public void SliceCountIsClamped(int value, int expected)
	{
		Assert.That(new VolumetricFogConfig { SliceCount = value }.SliceCount, Is.EqualTo(expected));
	}

	[TestCase(-1.0f, 0.0f)]
	[TestCase(0.5f, 0.5f)]
	[TestCase(1.0f, 0.999f)]
	public void HistoryWeightIsClamped(float value, float expected)
	{
		Assert.That(new VolumetricFogConfig { HistoryWeight = value }.HistoryWeight, Is.EqualTo(expected));
	}

	[Test]
	public void RenderConfigWithoutFogKeyDeserializesToDefaults()
	{
		var config = JsonSerializer.Deserialize<RenderConfig>("{}", AssetJson.SerializerOptions)!;
		Assert.That(config.VolumetricFog.Enabled, Is.False);
		Assert.That(config.VolumetricFog.SliceCount, Is.EqualTo(64));
	}

	[Test]
	public void FogConfigRoundTripsAndClampsSerializedValues()
	{
		var json = """{ "VolumetricFog": { "Enabled": true, "Extinction": 0.02, "SliceCount": 500, "FroxelPixelSize": 2, "HistoryWeight": 3 } }""";
		var fog = JsonSerializer.Deserialize<RenderConfig>(json, AssetJson.SerializerOptions)!.VolumetricFog;
		Assert.Multiple(() =>
		{
			Assert.That(fog.Enabled, Is.True);
			Assert.That(fog.Extinction, Is.EqualTo(0.02f));
			Assert.That(fog.SliceCount, Is.EqualTo(128));
			Assert.That(fog.FroxelPixelSize, Is.EqualTo(4));
			Assert.That(fog.HistoryWeight, Is.EqualTo(0.999f));
		});

		var roundTripped = JsonSerializer.Deserialize<RenderConfig>(
			JsonSerializer.Serialize(new RenderConfig { VolumetricFog = fog }, AssetJson.SerializerOptions),
			AssetJson.SerializerOptions)!.VolumetricFog;
		Assert.That(roundTripped.SliceCount, Is.EqualTo(128));
		Assert.That(roundTripped.Extinction, Is.EqualTo(0.02f));
	}

	[Test]
	public void GridCoversFramebufferWithRoundedUpFroxels()
	{
		var grid = VolumetricFogPass.ComputeGrid(new Int2(1081, 785), new VolumetricFogConfig());
		Assert.That(grid, Is.EqualTo(new Int3(68, 50, 64)));
	}

	[Test]
	public void Texture3DAllowsUnorderedAccessButNotRenderTargets()
	{
		Assert.DoesNotThrow(() => _ = FogDescriptor(8, 8, 4));
		Assert.Throws<ArgumentException>(() => _ = new TextureDescriptor(
			8, 8, TextureFormat.Rgba16Float, TextureUsage.ShaderResource | TextureUsage.RenderTarget,
			dimension: TextureDimension.Texture3D, depth: 4));
		Assert.Throws<ArgumentOutOfRangeException>(() => _ = new TextureDescriptor(
			8, 8, TextureFormat.Rgba16Float, TextureUsage.ShaderResource, depth: 4));
	}

	/// <summary>
	/// The fog grid at 16px froxels has the same width and height as bloom's fourth mip, with the same
	/// format and usage. If the transient alias planner treated them as compatible, a fog volume could be
	/// backed by a 2D texture and the whole frame would composite zero transmittance.
	/// </summary>
	[Test]
	public void TransientAliasingNeverSharesSlotsBetweenTextureDimensions()
	{
		var registry = CreateRegistry();
		var flat = registry.CreateTransientTexture(FlatDescriptor(68, 50));
		var volume = registry.CreateTransientTexture(FogDescriptor(68, 50, 64));
		var writeFlat = new RenderGraphPass("Flat", PassKind.Compute);
		writeFlat.AddWrite(flat, ResourceState.UnorderedAccess);
		var writeVolume = new RenderGraphPass("Volume", PassKind.Compute);
		writeVolume.AddWrite(volume, ResourceState.UnorderedAccess);

		new RenderGraphCompiler(registry).Compile([writeFlat, writeVolume]);

		Assert.That(registry.GetStateTrackingKey(volume), Is.Not.EqualTo(registry.GetStateTrackingKey(flat)));
	}

	[Test]
	public void TransientAliasingStillSharesSlotsForIdenticalVolumes()
	{
		var registry = CreateRegistry();
		var first = registry.CreateTransientTexture(FogDescriptor(68, 50, 64));
		var second = registry.CreateTransientTexture(FogDescriptor(68, 50, 64));
		var writeFirst = new RenderGraphPass("First", PassKind.Compute);
		writeFirst.AddWrite(first, ResourceState.UnorderedAccess);
		var writeSecond = new RenderGraphPass("Second", PassKind.Compute);
		writeSecond.AddWrite(second, ResourceState.UnorderedAccess);

		new RenderGraphCompiler(registry).Compile([writeFirst, writeSecond]);

		Assert.That(registry.GetStateTrackingKey(second), Is.EqualTo(registry.GetStateTrackingKey(first)));
	}

	[Test]
	public void TransientPoolNeverReturnsTextureOfAnotherDimension()
	{
		var registry = CreateRegistry();
		registry.BeginFrame();
		_ = registry.GetTexture(registry.CreateTransientTexture(FlatDescriptor(68, 50)));
		registry.EndFrame();
		registry.BeginFrame();
		var volume = registry.GetTexture(registry.CreateTransientTexture(FogDescriptor(68, 50, 64)));

		Assert.That(volume.Descriptor.Dimension, Is.EqualTo(TextureDimension.Texture3D));
		Assert.That(volume.Descriptor.Depth, Is.EqualTo(64));
	}

	[Test]
	public void DepthToSliceIsLogarithmicAndMatchesShaderBoundaries()
	{
		const int slices = 64;
		Assert.Multiple(() =>
		{
			Assert.That(VolumetricFogBinner.DepthToSlice(0.0f, Near, MaxDistance, slices), Is.EqualTo(0));
			Assert.That(VolumetricFogBinner.DepthToSlice(MaxDistance * 4.0f, Near, MaxDistance, slices), Is.EqualTo(slices - 1));
			Assert.That(VolumetricFogBinner.DepthToSlice(MathF.Sqrt(Near * MaxDistance) * 1.001f, Near, MaxDistance, slices), Is.EqualTo(slices / 2));
		});
		for (var slice = 0; slice < slices; slice++)
		{
			// Mirror of FogSliceBoundary in volumetric_fog.slang.
			var center = Near * MathF.Pow(MaxDistance / Near, (slice + 0.5f) / slices);
			Assert.That(VolumetricFogBinner.DepthToSlice(center, Near, MaxDistance, slices), Is.EqualTo(slice));
		}
	}

	[Test]
	public void BinnerPlacesVolumeInFrontOfCameraInCenterCells()
	{
		var grid = new Int3(64, 32, 64);
		var result = VolumetricFogBinner.Build(Scene(Volume(new Vector3(0.0f, 0.0f, 50.0f), 4.0f)), grid, MaxDistance);
		var cells = OccupiedCells(result);
		var expectedSlice = VolumetricFogBinner.DepthToSlice(50.0f, Near, MaxDistance, grid.Z) / VolumetricFogBinner.MacroCellSize;
		Assert.Multiple(() =>
		{
			Assert.That(result.Volumes, Has.Length.EqualTo(1));
			Assert.That(result.CellGrid, Is.EqualTo(new Int3(16, 8, 16)));
			Assert.That(cells, Is.Not.Empty);
			Assert.That(cells.All(cell => cell.X is >= 7 and <= 8 && cell.Y is >= 3 and <= 4), Is.True);
			Assert.That(cells.Any(cell => cell.Z == expectedSlice), Is.True);
			Assert.That(result.VolumeIndices.All(index => index == 0u), Is.True);
		});
	}

	[Test]
	public void BinnerUsesScreenSpaceRowsWithOriginAtTop()
	{
		var grid = new Int3(64, 32, 64);
		var above = OccupiedCells(VolumetricFogBinner.Build(Scene(Volume(new Vector3(0.0f, 20.0f, 50.0f), 4.0f)), grid, MaxDistance));
		var below = OccupiedCells(VolumetricFogBinner.Build(Scene(Volume(new Vector3(0.0f, -20.0f, 50.0f), 4.0f)), grid, MaxDistance));
		Assert.That(above.Max(cell => cell.Y), Is.LessThan(below.Min(cell => cell.Y)));
	}

	[Test]
	public void BinnerCullsVolumesBehindCameraOrBeyondMaxDistance()
	{
		var grid = new Int3(64, 32, 64);
		var behind = VolumetricFogBinner.Build(Scene(Volume(new Vector3(0.0f, 0.0f, -50.0f), 4.0f)), grid, MaxDistance);
		var beyond = VolumetricFogBinner.Build(Scene(Volume(new Vector3(0.0f, 0.0f, MaxDistance + 50.0f), 4.0f)), grid, MaxDistance);
		Assert.Multiple(() =>
		{
			Assert.That(behind.Volumes, Is.Empty);
			Assert.That(beyond.Volumes, Is.Empty);
			Assert.That(behind.CellHeaders.All(header => header.Count == 0), Is.True);
		});
	}

	[Test]
	public void BinnerCoversWholeScreenForVolumeContainingCamera()
	{
		var grid = new Int3(64, 32, 64);
		var result = VolumetricFogBinner.Build(Scene(Volume(Vector3.Zero, 20.0f)), grid, MaxDistance);
		var nearCells = OccupiedCells(result).Where(cell => cell.Z == 0).ToArray();
		Assert.That(nearCells, Has.Length.EqualTo(result.CellGrid.X * result.CellGrid.Y));
	}

	[Test]
	public void LightSelectionPrefersShadowedDirectionalLight()
	{
		var scene = Scene(
			[],
			[
				Light(LightType.Point, 9.0f),
				Light(LightType.Directional, 1.0f),
				Light(LightType.Directional, 2.0f)
			]);
		var (shadowed, _) = VolumetricFogPass.SelectDirectionalLight(scene, Shadow(1));
		var (unshadowed, direction) = VolumetricFogPass.SelectDirectionalLight(scene, Shadow(-1));
		Assert.Multiple(() =>
		{
			Assert.That(shadowed.W, Is.EqualTo(2.0f));
			Assert.That(unshadowed.W, Is.EqualTo(1.0f));
			Assert.That(direction.W, Is.EqualTo(1.0f));
		});
	}

	[Test]
	public void LightSelectionDisablesSunWithoutDirectionalLights()
	{
		var scene = Scene([], [Light(LightType.Point, 5.0f)]);
		var (color, direction) = VolumetricFogPass.SelectDirectionalLight(scene, Shadow(-1));
		Assert.That(color, Is.EqualTo(Vector4.Zero));
		Assert.That(direction.W, Is.EqualTo(0.0f));
	}

	[Test]
	public void DisabledFogSchedulesNoPasses()
	{
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, new VolumetricFogConfig { Enabled = false });
		builder.Build(graph);
		Assert.That(graph.Passes.Any(pass => pass.Name.StartsWith("Volumetric Fog", StringComparison.Ordinal)), Is.False);
		Assert.That(Resources(builder).FogIntegrated.IsValid, Is.False);
		Assert.DoesNotThrow(builder.CompleteFrame);
	}

	[Test]
	public void EnabledFogSchedulesStagesBeforeLightingWithHistoryPingPong()
	{
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, new VolumetricFogConfig { Enabled = true });
		builder.Build(graph);
		var resources = Resources(builder);
		var names = graph.Passes.Select(pass => pass.Name).ToList();
		var inject = graph.Passes.Single(pass => pass.Name == "Volumetric Fog Inject");
		var temporal = graph.Passes.Single(pass => pass.Name == "Volumetric Fog Temporal");
		var integrate = graph.Passes.Single(pass => pass.Name == "Volumetric Fog Integrate");
		var lighting = graph.Passes.Single(pass => pass.Name == "Deferred Lighting");
		Assert.Multiple(() =>
		{
			Assert.That(names.IndexOf("Volumetric Fog Inject"), Is.LessThan(names.IndexOf("Volumetric Fog Temporal")));
			Assert.That(names.IndexOf("Volumetric Fog Temporal"), Is.LessThan(names.IndexOf("Volumetric Fog Integrate")));
			Assert.That(names.IndexOf("Volumetric Fog Integrate"), Is.LessThan(names.IndexOf("Deferred Lighting")));
			Assert.That(inject.Writes, Does.Contain(resources.FogCurrent));
			Assert.That(temporal.Reads, Does.Contain(resources.FogCurrent));
			Assert.That(temporal.Reads, Does.Contain(resources.FogHistoryRead));
			Assert.That(temporal.Writes, Does.Contain(resources.FogHistoryWrite));
			Assert.That(integrate.Reads, Does.Contain(resources.FogHistoryWrite));
			Assert.That(integrate.Writes, Does.Contain(resources.FogIntegrated));
			Assert.That(lighting.Reads, Does.Contain(resources.FogIntegrated));
			Assert.That(resources.FogHistoryValid, Is.False);
		});

		var firstReadIndex = ViewState(builder).FogHistoryReadIndex;
		builder.CompleteFrame();
		Assert.That(ViewState(builder).FogHistoryReadIndex, Is.EqualTo(1 - firstReadIndex));
		BeginFrame(builder, new VolumetricFogConfig { Enabled = true });
		Assert.That(Resources(builder).FogHistoryValid, Is.True);
		builder.CompleteFrame();
	}

	[Test]
	public void FogHistoryIsInvalidatedByShapeChangeAndDisable()
	{
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, new VolumetricFogConfig { Enabled = true });
		builder.CompleteFrame();
		BeginFrame(builder, new VolumetricFogConfig { Enabled = true, SliceCount = 32 });
		Assert.That(Resources(builder).FogHistoryValid, Is.False);
		builder.CompleteFrame();
		BeginFrame(builder, new VolumetricFogConfig { Enabled = true, SliceCount = 32, MaxDistance = 100.0f });
		Assert.That(Resources(builder).FogHistoryValid, Is.False);
		builder.CompleteFrame();

		BeginFrame(builder, new VolumetricFogConfig { Enabled = false });
		Assert.That(ViewState(builder).FogHistoryTextures[0], Is.Null);
		builder.CompleteFrame();
		BeginFrame(builder, new VolumetricFogConfig { Enabled = true, SliceCount = 32, MaxDistance = 100.0f });
		Assert.That(Resources(builder).FogHistoryValid, Is.False);
	}

	/// <summary>Registry over a device that creates textures matching each descriptor and retires immediately.</summary>
	private static RenderGraphResourceRegistry CreateRegistry()
	{
		var device = new Mock<IGfxDevice>();
		device.Setup(value => value.CreateTexture(in It.Ref<TextureDescriptor>.IsAny))
			.Returns((in TextureDescriptor descriptor) =>
			{
				var texture = new Mock<IGfxTexture>();
				texture.SetupGet(value => value.Descriptor).Returns(descriptor);
				return texture.Object;
			});
		device.Setup(value => value.Retire(It.IsAny<Action>(), It.IsAny<string?>()))
			.Callback<Action, string?>((release, _) => release());
		var registry = new RenderGraphResourceRegistry();
		registry.SetDevice(device.Object);
		return registry;
	}

	private static TextureDescriptor FogDescriptor(int width, int height, int depth) => new(
		width, height, TextureFormat.Rgba16Float, TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
		new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f), dimension: TextureDimension.Texture3D, depth: depth);

	private static TextureDescriptor FlatDescriptor(int width, int height) => new(
		width, height, TextureFormat.Rgba16Float, TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
		new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f));

	private static FogVolumePacket Volume(Vector3 position, float size) => new(
		new FogVolume { Size = new Vector3(size) }, Matrix4x4.CreateTranslation(position));

	private static LightPacket Light(LightType type, float intensity) => new(
		new Light { Type = type, Intensity = intensity, Color = new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f) },
		Matrix4x4.CreateRotationX(MathF.PI / 4.0f));

	private static ShadowFrameData Shadow(int shadowedIndex) => new(
		shadowedIndex >= 0, 3, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
		10.0f, 30.0f, 100.0f, 2.0f, 100.0f, shadowedIndex, Vector3.Zero, 1.0f, 2048);

	private static SceneDrawData Scene(params FogVolumePacket[] volumes) => Scene(volumes, []);

	/// <summary>Camera at the origin looking down +Z, matching the engine's left-handed view space.</summary>
	private static SceneDrawData Scene(FogVolumePacket[] volumes, LightPacket[] lights)
	{
		var projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 3.0f, 2.0f, Near, 1000.0f);
		Matrix4x4.Invert(projection, out var inverseProjection);
		return new SceneDrawData(
			Matrix4x4.Identity, projection, projection, projection, projection, projection,
			inverseProjection, inverseProjection, Vector3.Zero, Vector3.Zero, new Int2(1024, 512),
			Near, 1000.0f, Vector2.Zero, Vector2.Zero, Vector2.Zero, resetHistory: false,
			lights, Array.Empty<DecalProjectorPacket>(), volumes, Array.Empty<OutlinePacket>());
	}

	private static List<Int3> OccupiedCells(VolumetricFogBinningResult result)
	{
		var cells = new List<Int3>();
		var grid = result.CellGrid;
		for (var i = 0; i < result.CellHeaders.Length; i++)
		{
			if (result.CellHeaders[i].Count == 0) continue;
			cells.Add(new Int3(i % grid.X, i / grid.X % grid.Y, i / (grid.X * grid.Y)));
		}
		return cells;
	}

	private static void BeginFrame(RenderGraphFrameBuilder builder, VolumetricFogConfig fog)
	{
		var config = new RenderConfig
		{
			VolumetricFog = fog,
			AntiAliasing = new AntiAliasingConfig { Enabled = false },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = false },
			Reflections = new ReflectionConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false }
		};
		builder.BeginFrame(new Int2(64, 64), new Int2(64, 64), default, true, false, Vector3.UnitY, 1.0f, config, Vector3.Zero);
	}

	private static RenderGraphFrameResources Resources(RenderGraphFrameBuilder builder) => GetField<RenderGraphFrameResources>(builder, "_frameResources");

	/// <summary>The per-view state the builder is currently recording into.</summary>
	private static RenderViewState ViewState(RenderGraphFrameBuilder builder) => GetField<RenderViewState>(builder, "_view");
	private static T GetField<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
}
