using System.Numerics;
using System.Text.Json;
using WolfEngine;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RayTracingSceneResourcesTests
{
	[Test]
	public void Aces2SdrTonemappingAndPresentationShadersCompileForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var tonemapping = shaderCompiler.GetComputeShaderWithReflection(
			ShaderPath("tonemapping.compute.slang"),
			"TonemappingCS",
			GraphicsBackendKind.Metal);
		var presentation = shaderCompiler.GetComputeShaderWithReflection(
			ShaderPath("copy_to_final.compute.slang"),
			"CopyToFinalCS",
			GraphicsBackendKind.Metal);

		Assert.That(tonemapping.Bytecode.IsEmpty, Is.False);
		Assert.That(presentation.Bytecode.IsEmpty, Is.False);
		Assert.That(
			presentation.ReflectionLayout.GetConstantBuffer("BindlessHandles")
				.GetFieldOrThrow("encodedSceneOutputHandle").ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.UInt));
	}

	[Test]
	public void Aces2SdrTonemappingAndPresentationShadersCompileForD3D12()
	{
		if (OperatingSystem.IsWindows() == false)
		{
			Assert.Ignore("DirectX shader validation only runs on Windows.");
		}

		var shaderCompiler = new ShaderCompiler();
		foreach (var shader in new[]
		{
			(Name: "tonemapping.compute.slang", EntryPoint: "TonemappingCS"),
			(Name: "copy_to_final.compute.slang", EntryPoint: "CopyToFinalCS")
		})
		{
			var compiled = shaderCompiler.GetComputeShaderWithReflection(
				ShaderPath(shader.Name),
				shader.EntryPoint,
				GraphicsBackendKind.D3D12);
			Assert.That(compiled.Bytecode.IsEmpty, Is.False, shader.Name);
		}
	}

	[Test]
	public void GpuDrawCullShaderCompilesWithMultiViewLayoutForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetComputeShaderWithReflection(
			ShaderPath("gpu_draw_cull.compute.slang"),
			"CSCull",
			GraphicsBackendKind.Metal);
		var cullParams = compiled.ReflectionLayout.GetConstantBuffer("CullParams");

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(64));
		Assert.That(cullParams.GetFieldOrThrow("planes[17]").ValueKind, Is.EqualTo(ShaderConstantFieldValueKind.Vector4));
		Assert.That(cullParams.GetFieldOrThrow("viewCount").ValueKind, Is.EqualTo(ShaderConstantFieldValueKind.UInt));
		Assert.That(cullParams.GetFieldOrThrow("outputDrawArgsStride").ValueKind, Is.EqualTo(ShaderConstantFieldValueKind.UInt));
		Assert.That(cullParams.GetFieldOrThrow("outputLaneStride").ValueKind, Is.EqualTo(ShaderConstantFieldValueKind.UInt));
		Assert.That(cullParams.GetFieldOrThrow("participatingLaneMask").ValueKind, Is.EqualTo(ShaderConstantFieldValueKind.UInt));
		Assert.That(compiled.ReflectionLayout.GetResource("g_DrawArgs").RegisterIndex, Is.EqualTo(3u));
		Assert.That(compiled.ReflectionLayout.GetResource("g_DrawExecutionRangePerBucket").RegisterIndex, Is.EqualTo(5u));
		Assert.That(compiled.ReflectionLayout.GetResource("g_Diagnostics").RegisterIndex, Is.EqualTo(10u));
	}

	[Test]
	public void RtaoShaderCompilesForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetComputeShaderWithReflection(
			ShaderPath("ao_rtao.compute.slang"),
			"AmbientOcclusionRayTracedCS",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));
	}

	[Test]
	public void TerrainRayTracingVertexUpdateShaderCompilesForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetComputeShaderWithReflection(
			ShaderPath("terrain_rt_vertex_update.compute.slang"),
			"TerrainRayTracingVertexUpdateCS",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(64));
		Assert.That(compiled.ReflectionLayout.GetConstantBuffer("TerrainRtVertexUpdateParams"), Is.Not.Null);
	}

	[Test]
	public void NamedComputeShadersCompileForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		foreach (var shader in new[]
		{
			(Name: "ao_rtao.compute.slang", EntryPoint: "AmbientOcclusionRayTracedCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "ao_vbao.compute.slang", EntryPoint: "AmbientOcclusionVisibilityBitmaskCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "ao_blur.compute.slang", EntryPoint: "AmbientOcclusionBlurCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "ao_upsample.compute.slang", EntryPoint: "AmbientOcclusionUpsampleCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "cas_sharpen.compute.slang", EntryPoint: "CasSharpenCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "bloom.compute.slang", EntryPoint: "BloomPrefilterCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "bloom.compute.slang", EntryPoint: "BloomDownsampleCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "bloom.compute.slang", EntryPoint: "BloomUpsampleCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "bloom.compute.slang", EntryPoint: "BloomCompositeCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "copy_to_final.compute.slang", EntryPoint: "CopyToFinalCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "deferred_lighting.compute.slang", EntryPoint: "DeferredLightingCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "gbuffer_decal_seed.compute.slang", EntryPoint: "GBufferDecalSeedCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "reflections_ssr.compute.slang", EntryPoint: "ReflectionsScreenSpaceCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "reflections_rt.compute.slang", EntryPoint: "ReflectionsRayTracedCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "taa_history_store.compute.slang", EntryPoint: "TaaHistoryStoreCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "taa_resolve.compute.slang", EntryPoint: "TaaResolveCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "terrain_rt_vertex_update.compute.slang", EntryPoint: "TerrainRayTracingVertexUpdateCS", ThreadsX: 64u, ThreadsY: 1u),
			(Name: "tonemapping.compute.slang", EntryPoint: "TonemappingCS", ThreadsX: 8u, ThreadsY: 8u)
		})
		{
			var compiled = shaderCompiler.GetComputeShaderWithReflection(
				ShaderPath(shader.Name),
				shader.EntryPoint,
				GraphicsBackendKind.Metal);

			Assert.That(compiled.Bytecode.IsEmpty, Is.False, shader.Name);
			Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(shader.ThreadsX), shader.Name);
			Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(shader.ThreadsY), shader.Name);
		}
	}

	[Test]
	public void DdgiShadersCompileForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		foreach (var shader in new[]
		{
			(Name: "ddgi_classify.compute.slang", EntryPoint: "DdgiProbeClassifyCS", ThreadsX: 64u, ThreadsY: 1u),
			(Name: "ddgi_trace.compute.slang", EntryPoint: "DdgiProbeTraceCS", ThreadsX: 64u, ThreadsY: 1u),
			(Name: "ddgi_trace.compute.slang", EntryPoint: "DdgiRelocationTraceCS", ThreadsX: 16u, ThreadsY: 1u),
			(Name: "ddgi_relocate.compute.slang", EntryPoint: "DdgiRelocationSolveCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "ddgi_irradiance_integrate.compute.slang", EntryPoint: "DdgiIrradianceIntegrateCS", ThreadsX: 8u, ThreadsY: 8u),
			(Name: "ddgi_integrate.compute.slang", EntryPoint: "DdgiVisibilityIntegrateCS", ThreadsX: 16u, ThreadsY: 16u)
		})
		{
			var compiled = shaderCompiler.GetComputeShaderWithReflection(
				ShaderPath(shader.Name),
				shader.EntryPoint,
				GraphicsBackendKind.Metal);

			Assert.That(compiled.Bytecode.IsEmpty, Is.False, shader.Name);
			Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(shader.ThreadsX), shader.Name);
			Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(shader.ThreadsY), shader.Name);
			Assert.That(
				compiled.ReflectionLayout
					.GetConstantBuffer("DdgiSettings")
					.GetFieldOrThrow("scrollDeltaX")
					.ValueKind,
				Is.EqualTo(ShaderConstantFieldValueKind.Int),
				shader.Name);
			Assert.That(
				compiled.ReflectionLayout
					.GetConstantBuffer("DdgiSettings")
					.GetFieldOrThrow("debugProbeRelocationReadbackIndex")
					.ValueKind,
				Is.EqualTo(ShaderConstantFieldValueKind.UInt),
				shader.Name);
		}
	}

	[Test]
	public void RayTracingShadersCompileForD3D12()
	{
		if (OperatingSystem.IsWindows() == false)
		{
			Assert.Ignore("DirectX shader validation only runs on Windows.");
		}
		
		var shaderCompiler = new ShaderCompiler();
		foreach (var shader in new[]
		{
			(Name: "ao_rtao.compute.slang", EntryPoint: "AmbientOcclusionRayTracedCS"),
			(Name: "ddgi_classify.compute.slang", EntryPoint: "DdgiProbeClassifyCS"),
			(Name: "ddgi_trace.compute.slang", EntryPoint: "DdgiProbeTraceCS"),
			(Name: "ddgi_trace.compute.slang", EntryPoint: "DdgiRelocationTraceCS"),
			(Name: "ddgi_relocate.compute.slang", EntryPoint: "DdgiRelocationSolveCS"),
			(Name: "ddgi_irradiance_integrate.compute.slang", EntryPoint: "DdgiIrradianceIntegrateCS"),
			(Name: "ddgi_integrate.compute.slang", EntryPoint: "DdgiVisibilityIntegrateCS")
		})
		{
			var compiled = shaderCompiler.GetComputeShaderWithReflection(
				ShaderPath(shader.Name),
				shader.EntryPoint,
				GraphicsBackendKind.D3D12);
			Assert.That(compiled.Bytecode.IsEmpty, Is.False, shader.Name);
		}
	}

	[Test]
	public void ReflectionShadersCompileForD3D12()
	{
		if (OperatingSystem.IsWindows() == false)
		{
			Assert.Ignore("DirectX shader validation only runs on Windows.");
		}

		var shaderCompiler = new ShaderCompiler();
		foreach (var shader in new[]
		{
			(Name: "reflections_ssr.compute.slang", EntryPoint: "ReflectionsScreenSpaceCS"),
			(Name: "reflections_rt.compute.slang", EntryPoint: "ReflectionsRayTracedCS")
		})
		{
			var compiled = shaderCompiler.GetComputeShaderWithReflection(
				ShaderPath(shader.Name),
				shader.EntryPoint,
				GraphicsBackendKind.D3D12);
			Assert.That(compiled.Bytecode.IsEmpty, Is.False, shader.Name);
			Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8), shader.Name);
			Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8), shader.Name);
			Assert.That(
				compiled.ReflectionLayout.GetConstantBuffer("ReflectionSettings")
					.GetFieldOrThrow("maxRoughness").ValueKind,
				Is.EqualTo(ShaderConstantFieldValueKind.Float),
				shader.Name);
		}
	}

	[Test]
	public void DdgiProbeDebugShaderCompilesForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetGraphicsShaderWithReflection(
			ShaderPath("debug_primitive_forward.slang"),
			"vertexShader",
			"fragmentShader",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.Vertex, Is.Not.Null);
		Assert.That(compiled.Bytecode.Pixel, Is.Not.Null);
		Assert.That(
			compiled.ReflectionLayout
				.GetConstantBuffer("DdgiDebugParams")
				.GetFieldOrThrow("ddgiProbeStateHandle")
				.ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.UInt));
		Assert.That(
			SharedDrawGraphicsBufferBindings
				.FromTransparentReflection(compiled.ReflectionLayout)
				.DdgiDebugRegisterIndex,
			Is.EqualTo(4u));
	}

	[TestCase(0.0f, 0.0f, 0.25f, true)]
	[TestCase(2.0f, 0.0f, 0.25f, false)]
	[TestCase(1.25f, 0.0f, 0.25f, true)]
	[TestCase(3.0f, 0.0f, 2.0f, true)]
	[TestCase(1.15f, 1.2f, 0.26f, true)]
	public void DdgiProbeInfluenceUsesConservativeSphereAabbIntersection(
		float sphereCenterX,
		float sphereCenterY,
		float sphereRadius,
		bool expected)
	{
		Assert.That(
			DdgiUtilities.SphereIntersectsProbeInfluence(
				new Vector3(sphereCenterX, sphereCenterY, 0.0f),
				sphereRadius,
				Vector3.Zero,
				influenceHalfExtent: 1.0f),
			Is.EqualTo(expected));
	}

	[Test]
	public void DdgiProbeInfluenceExpandsByViewBias()
	{
		Assert.That(DdgiUtilities.GetProbeInfluenceHalfExtent(2.0f, 0.25f), Is.EqualTo(2.25f));
		Assert.That(DdgiUtilities.GetProbeInfluenceHalfExtent(0.0f, -1.0f), Is.EqualTo(0.001f));
		Assert.That(
			DdgiUtilities.SphereIntersectsProbeInfluence(
				new Vector3(2.2f, 0.0f, 0.0f),
				0.01f,
				Vector3.Zero,
				DdgiUtilities.GetProbeInfluenceHalfExtent(2.0f, 0.0f)),
			Is.False);
		Assert.That(
			DdgiUtilities.SphereIntersectsProbeInfluence(
				new Vector3(2.2f, 0.0f, 0.0f),
				0.01f,
				Vector3.Zero,
				DdgiUtilities.GetProbeInfluenceHalfExtent(2.0f, 0.25f)),
			Is.True);
	}

	[TestCase(false, false, true, 1, false)]
	[TestCase(true, false, true, 1, true)]
	[TestCase(true, true, false, 1, true)]
	[TestCase(true, true, true, 1, false)]
	[TestCase(true, true, true, 2, true)]
	public void DdgiGeometryAwareSchedulingHandlesEnableTransitions(
		bool enabled,
		bool previouslyEnabled,
		bool hasHistory,
		int frameSlot,
		bool expected)
	{
		Assert.That(
			DdgiUtilities.IsProbeUpdateActive(
				probeIndex: 2,
				probeUpdateFrames: 4,
				probeUpdateFrameIndex: frameSlot,
				forceFullUpdate: false,
				enabled: enabled,
				previouslyEnabled: previouslyEnabled,
				hasHistory: hasHistory),
			Is.EqualTo(expected));
	}

	[Test]
	public void DeferredLightingShaderCompilesForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetComputeShaderWithReflection(
			ShaderPath("deferred_lighting.compute.slang"),
			"DeferredLightingCS",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));

		var lightingLayout = compiled.ReflectionLayout.GetConstantBuffer("LightingParams");
		var scrollDeltaField = lightingLayout.GetFieldOrThrow("ddgiScrollDeltaX");
		Assert.That(scrollDeltaField.ValueKind, Is.EqualTo(ShaderConstantFieldValueKind.Int));

		var writer = new ShaderPropertyWriter(lightingLayout);
		writer.SetInt(scrollDeltaField, -7);
		Assert.That(
			BitConverter.ToInt32(writer.AsBytes().Slice(scrollDeltaField.Offset, sizeof(int))),
			Is.EqualTo(-7));
	}

	[Test]
	public void BloomDefaultsAndSettingsRoundTripThroughAssetJson()
	{
		var defaults = new RenderConfig().Bloom;
		Assert.That(defaults.Enabled, Is.True);
		Assert.That(defaults.Threshold, Is.EqualTo(1.0f));
		Assert.That(defaults.SoftKnee, Is.EqualTo(0.5f));
		Assert.That(defaults.Intensity, Is.EqualTo(0.08f));
		Assert.That(defaults.Scatter, Is.EqualTo(0.7f));
		Assert.That(defaults.Tint, Is.EqualTo(Vector3.One));
		Assert.That(defaults.Quality, Is.EqualTo(BloomQuality.High));

		var config = new RenderConfig { Bloom = new BloomConfig
		{
			Enabled = false, Threshold = 2.5f, SoftKnee = 0.25f, Intensity = 0.15f,
			Scatter = 0.4f, Tint = new Vector3(1.0f, 0.5f, 0.25f), Quality = BloomQuality.Low
		}};
		var roundTripped = JsonSerializer.Deserialize<RenderConfig>(
			JsonSerializer.Serialize(config, AssetJson.SerializerOptions), AssetJson.SerializerOptions)!;
		Assert.That(roundTripped.Bloom.Enabled, Is.False);
		Assert.That(roundTripped.Bloom.Threshold, Is.EqualTo(2.5f));
		Assert.That(roundTripped.Bloom.SoftKnee, Is.EqualTo(0.25f));
		Assert.That(roundTripped.Bloom.Intensity, Is.EqualTo(0.15f));
		Assert.That(roundTripped.Bloom.Scatter, Is.EqualTo(0.4f));
		Assert.That(roundTripped.Bloom.Tint, Is.EqualTo(new Vector3(1.0f, 0.5f, 0.25f)));
		Assert.That(roundTripped.Bloom.Quality, Is.EqualTo(BloomQuality.Low));
	}

	[Test]
	public void ReflectionDefaultsAndSettingsRoundTripThroughAssetJson()
	{
		var defaults = new RenderConfig().Reflections;
		Assert.That(defaults.Enabled, Is.True);
		Assert.That(defaults.Mode, Is.EqualTo(ReflectionMode.ScreenSpace));
		Assert.That(defaults.ScreenSpaceSettings.MaxSteps, Is.EqualTo(48));
		Assert.That(defaults.ScreenSpaceSettings.BinarySearchSteps, Is.EqualTo(5));
		Assert.That(defaults.RayTracedSettings.MaxRayDistance, Is.EqualTo(100.0f));

		var config = new RenderConfig
		{
			Reflections = new ReflectionConfig
			{
				Enabled = true,
				Mode = ReflectionMode.RayTraced,
				ScreenSpaceSettings = new ScreenSpaceReflectionSettings
				{
					MaxSteps = 24,
					BinarySearchSteps = 3,
					MaxRayDistance = 20.0f,
					Thickness = 0.25f,
					Bias = 0.04f,
					MaxRoughness = 0.5f,
					EdgeFade = 0.1f,
					Intensity = 0.8f
				},
				RayTracedSettings = new RayTracedReflectionSettings
				{
					MaxRayDistance = 75.0f,
					Bias = 0.05f,
					MaxRoughness = 0.7f,
					ScreenReuseThickness = 0.3f,
					Intensity = 0.9f
				}
			}
		};
		var roundTripped = JsonSerializer.Deserialize<RenderConfig>(
			JsonSerializer.Serialize(config, AssetJson.SerializerOptions),
			AssetJson.SerializerOptions)!;

		Assert.That(roundTripped.Reflections.Mode, Is.EqualTo(ReflectionMode.RayTraced));
		Assert.That(roundTripped.Reflections.ScreenSpaceSettings.MaxSteps, Is.EqualTo(24));
		Assert.That(roundTripped.Reflections.ScreenSpaceSettings.Thickness, Is.EqualTo(0.25f));
		Assert.That(roundTripped.Reflections.RayTracedSettings.MaxRayDistance, Is.EqualTo(75.0f));
		Assert.That(roundTripped.Reflections.RayTracedSettings.ScreenReuseThickness, Is.EqualTo(0.3f));
		Assert.That(roundTripped.Reflections.RayTracedSettings.Intensity, Is.EqualTo(0.9f));
	}

	private static string ShaderPath(string relativePath) => Path.GetFullPath(Path.Combine(
		TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "WolfEngine", "Shaders", relativePath));

	[Test]
	public void DdgiDefaultsAndAtlasSizingMatchMilestoneDefaults()
	{
		var config = new RenderConfig();
		var ddgi = config.DiffuseGlobalIllumination;

		Assert.That(ddgi.Enabled, Is.False);
		Assert.That(ddgi.Mode, Is.EqualTo(DiffuseGlobalIlluminationMode.RayTracedDdgi));
		Assert.That(ddgi.ProbeCounts.X, Is.EqualTo(16));
		Assert.That(ddgi.ProbeCounts.Y, Is.EqualTo(8));
		Assert.That(ddgi.ProbeCounts.Z, Is.EqualTo(16));
		Assert.That(ddgi.ProbeSpacing, Is.EqualTo(2.0f));
		Assert.That(ddgi.RaysPerProbe, Is.EqualTo(64));
		Assert.That(ddgi.ProbeUpdateFrames, Is.EqualTo(8));
		Assert.That(ddgi.MaxRayDistance, Is.EqualTo(6.0f));
		Assert.That(ddgi.NormalBias, Is.EqualTo(0.05f));
		Assert.That(ddgi.ViewBias, Is.EqualTo(0.2f));
		Assert.That(ddgi.HorizontalBlendDistance, Is.EqualTo(6.0f));
		Assert.That(ddgi.VerticalBlendDistance, Is.EqualTo(6.0f));
		Assert.That(ddgi.IrradianceTemporalBlendSpeed, Is.EqualTo(0.08f));
		Assert.That(ddgi.Hysteresis, Is.EqualTo(0.95f));
		Assert.That(ddgi.RecursiveBounceEnergy, Is.EqualTo(DdgiUtilities.DefaultRecursiveBounceEnergy));
		Assert.That(ddgi.ProbeRelocationEnabled, Is.True);
		Assert.That(ddgi.ProbeMinFrontfaceDistance, Is.EqualTo(0.2f));
		Assert.That(ddgi.ProbeBackfaceThreshold, Is.EqualTo(0.25f));
		Assert.That(ddgi.ProbeMaxRelocationDistanceFactor, Is.EqualTo(0.45f));
			Assert.That(ddgi.DebugProbeSpheres, Is.False);
			Assert.That(ddgi.DebugProbeSphereRadius, Is.EqualTo(0.15f));
			Assert.That(ddgi.DebugFirstProbeRelocationReadback, Is.False);
			Assert.That(ddgi.DebugProbeRelocationReadbackIndex, Is.EqualTo(0));

		var shape = DdgiUtilities.GetGridShape(ddgi);
		Assert.That(shape.ProbeCount, Is.EqualTo(2048));
		Assert.That(DdgiUtilities.GetAtlasSize(shape, DdgiUtilities.IrradianceTileInteriorSize), Is.EqualTo(new Int2(460, 450)));
		Assert.That(DdgiUtilities.GetAtlasSize(shape, DdgiUtilities.VisibilityTileInteriorSize), Is.EqualTo(new Int2(828, 810)));
	}

	[Test]
	public void RenderConfig_DdgiSettingsRoundTripThroughAssetJson()
	{
		var config = new RenderConfig
		{
			DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
			{
				Enabled = true,
				Mode = DiffuseGlobalIlluminationMode.RayTracedDdgi,
				Origin = new Vector3(1.0f, 2.0f, 3.0f),
				ProbeCounts = new DdgiProbeCounts { X = 4, Y = 5, Z = 6 },
				ProbeSpacing = 3.5f,
				RaysPerProbe = 32,
				ProbeUpdateFrames = 4,
				MaxRayDistance = 12.0f,
					NormalBias = 0.1f,
					ViewBias = 0.4f,
					HorizontalBlendDistance = 8.0f,
					VerticalBlendDistance = 4.0f,
					IrradianceTemporalBlendSpeed = 0.12f,
					Hysteresis = 0.8f,
					RecursiveBounceEnergy = 0.35f,
					ProbeRelocationEnabled = false,
					ProbeMinFrontfaceDistance = 0.3f,
					ProbeBackfaceThreshold = 0.4f,
					ProbeMaxRelocationDistanceFactor = 0.35f,
					DebugProbeSpheres = true,
					DebugProbeSphereRadius = 0.3f,
					DebugFirstProbeRelocationReadback = true,
					DebugProbeRelocationReadbackIndex = 37
			},
			ShadowMaps = new ShadowMapConfig
			{
				CascadeCount = 2,
				CascadeResolution = 1024,
				CascadeBlendDistance = 3.5f,
				MaxDistance = 96.0f,
				DepthBias = 0.08f
			}
		};

		var json = JsonSerializer.Serialize(config, AssetJson.SerializerOptions);
		var roundTripped = JsonSerializer.Deserialize<RenderConfig>(json, AssetJson.SerializerOptions)!;
		var ddgi = roundTripped.DiffuseGlobalIllumination;

		Assert.That(ddgi.Enabled, Is.True);
		Assert.That(ddgi.Mode, Is.EqualTo(DiffuseGlobalIlluminationMode.RayTracedDdgi));
		Assert.That(ddgi.Origin, Is.EqualTo(new Vector3(1.0f, 2.0f, 3.0f)));
		Assert.That(ddgi.ProbeCounts.X, Is.EqualTo(4));
		Assert.That(ddgi.ProbeCounts.Y, Is.EqualTo(5));
		Assert.That(ddgi.ProbeCounts.Z, Is.EqualTo(6));
		Assert.That(ddgi.ProbeSpacing, Is.EqualTo(3.5f));
		Assert.That(ddgi.RaysPerProbe, Is.EqualTo(32));
		Assert.That(ddgi.ProbeUpdateFrames, Is.EqualTo(4));
		Assert.That(ddgi.MaxRayDistance, Is.EqualTo(12.0f));
		Assert.That(ddgi.NormalBias, Is.EqualTo(0.1f));
		Assert.That(ddgi.ViewBias, Is.EqualTo(0.4f));
		Assert.That(ddgi.HorizontalBlendDistance, Is.EqualTo(8.0f));
		Assert.That(ddgi.VerticalBlendDistance, Is.EqualTo(4.0f));
		Assert.That(ddgi.IrradianceTemporalBlendSpeed, Is.EqualTo(0.12f));
		Assert.That(ddgi.Hysteresis, Is.EqualTo(0.8f));
		Assert.That(ddgi.RecursiveBounceEnergy, Is.EqualTo(0.35f));
		Assert.That(ddgi.ProbeRelocationEnabled, Is.False);
		Assert.That(ddgi.ProbeMinFrontfaceDistance, Is.EqualTo(0.3f));
		Assert.That(ddgi.ProbeBackfaceThreshold, Is.EqualTo(0.4f));
		Assert.That(ddgi.ProbeMaxRelocationDistanceFactor, Is.EqualTo(0.35f));
			Assert.That(ddgi.DebugProbeSpheres, Is.True);
			Assert.That(ddgi.DebugProbeSphereRadius, Is.EqualTo(0.3f));
			Assert.That(ddgi.DebugFirstProbeRelocationReadback, Is.True);
			Assert.That(ddgi.DebugProbeRelocationReadbackIndex, Is.EqualTo(37));
		Assert.That(roundTripped.ShadowMaps.CascadeCount, Is.EqualTo(2));
		Assert.That(roundTripped.ShadowMaps.CascadeResolution, Is.EqualTo(1024));
		Assert.That(roundTripped.ShadowMaps.CascadeBlendDistance, Is.EqualTo(3.5f));
		Assert.That(roundTripped.ShadowMaps.MaxDistance, Is.EqualTo(96.0f));
		Assert.That(roundTripped.ShadowMaps.DepthBias, Is.EqualTo(0.08f));
	}

	[Test]
	public void DdgiProbeUpdateFramesClampToAtLeastOne()
	{
		var config = new DiffuseGlobalIlluminationConfig
		{
			ProbeUpdateFrames = 0
		};

		Assert.That(DdgiUtilities.GetProbeUpdateFrames(config), Is.EqualTo(1));
		Assert.That(DdgiUtilities.GetProbeUpdateFrameIndex(5, 0), Is.EqualTo(0));
		Assert.That(DdgiUtilities.IsProbeActive(3, 0, 0, forceFullUpdate: false), Is.True);
		Assert.That(DdgiUtilities.GetActiveProbeCount(17, 0, 0, forceFullUpdate: false), Is.EqualTo(17));
	}

	[TestCase(0, DdgiUtilities.IrradianceTileInteriorSize, 1)]
	[TestCase(32, DdgiUtilities.IrradianceTileInteriorSize, 32)]
	[TestCase(1024, DdgiUtilities.IrradianceTileInteriorSize, 64)]
	[TestCase(1024, DdgiUtilities.VisibilityTileInteriorSize, 256)]
	public void DdgiRaySampleCountDoesNotExceedTemporaryTileCapacity(
		int requestedRayCount,
		int tileInteriorSize,
		int expectedRayCount)
	{
		Assert.That(
			DdgiUtilities.GetRaySampleCount(requestedRayCount, tileInteriorSize),
			Is.EqualTo(expectedRayCount));
	}

	[TestCase(1, 1)]
	[TestCase(64, 64)]
	[TestCase(256, 320)]
	public void DdgiProbeTraceInvocationCountMergesOnlyIdenticalRaySets(
		int requestedRayCount,
		int expectedInvocationCount)
	{
		Assert.That(
			DdgiUtilities.GetProbeTraceInvocationCount(requestedRayCount),
			Is.EqualTo(expectedInvocationCount));
	}

	[Test]
	public void DdgiRelocationTraceRequiresEnabledDdgiAndRelocation()
	{
		var config = new RenderConfig
		{
			DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
			{
				Enabled = true,
				Mode = DiffuseGlobalIlluminationMode.RayTracedDdgi,
				ProbeRelocationEnabled = false
			}
		};

		Assert.That(DdgiUtilities.IsRelocationTraceEnabled(config), Is.False);
		config.DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
		{
			Enabled = true,
			Mode = DiffuseGlobalIlluminationMode.RayTracedDdgi,
			ProbeRelocationEnabled = true
		};
		Assert.That(DdgiUtilities.IsRelocationTraceEnabled(config), Is.True);
		config.DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
		{
			Enabled = false,
			Mode = DiffuseGlobalIlluminationMode.RayTracedDdgi,
			ProbeRelocationEnabled = true
		};
		Assert.That(DdgiUtilities.IsRelocationTraceEnabled(config), Is.False);
	}

	[Test]
	public void DdgiProbeBatchingUpdatesEveryProbeOncePerCycle()
	{
		const int probeCount = 10;
		const int updateFrames = 4;
		var updateCounts = new int[probeCount];

		for (uint frameIndex = 0; frameIndex < updateFrames; frameIndex++)
		{
			var frameSlot = DdgiUtilities.GetProbeUpdateFrameIndex(frameIndex, updateFrames);
			var activeCount = 0;
			for (var probeIndex = 0; probeIndex < probeCount; probeIndex++)
			{
				if (DdgiUtilities.IsProbeActive(probeIndex, updateFrames, frameSlot, forceFullUpdate: false) == false)
				{
					continue;
				}

				updateCounts[probeIndex]++;
				activeCount++;
			}

			Assert.That(activeCount, Is.EqualTo(DdgiUtilities.GetActiveProbeCount(probeCount, updateFrames, frameSlot, forceFullUpdate: false)));
		}

		Assert.That(updateCounts, Is.All.EqualTo(1));
	}

	[Test]
	public void DdgiProbeBatchingForceFullUpdateMarksAllProbesActive()
	{
		const int probeCount = 10;
		const int updateFrames = 4;

		Assert.That(DdgiUtilities.GetActiveProbeCount(probeCount, updateFrames, 2, forceFullUpdate: true), Is.EqualTo(probeCount));
		for (var probeIndex = 0; probeIndex < probeCount; probeIndex++)
		{
			Assert.That(DdgiUtilities.IsProbeActive(probeIndex, updateFrames, 2, forceFullUpdate: true), Is.True);
		}
	}

	[Test]
	public void DdgiShCoefficientTexturesUseOneTexelPerProbe()
	{
		var shape = DdgiUtilities.GetGridShape(new DiffuseGlobalIlluminationConfig
		{
			ProbeCounts = new DdgiProbeCounts { X = 4, Y = 3, Z = 2 }
		});

		var size = DdgiUtilities.GetShCoefficientTextureSize(shape);

		Assert.That(DdgiUtilities.ShCoefficientCount, Is.EqualTo(4));
		Assert.That(size.X, Is.EqualTo(shape.AtlasColumns));
		Assert.That(size.Y, Is.EqualTo(shape.AtlasRows));
	}

	[Test]
	public void DdgiRuntimeOriginSnapsOnlyAfterCameraCrossesHalfSpacing()
	{
		var shape = new DdgiGridShape(4, 2, 4, 32, 6, 6);
		var anchor = new Vector3(1.0f, 2.0f, 3.0f);
		const float spacing = 2.0f;
		var initialCenter = anchor + new Vector3(3.0f, 1.0f, 3.0f);

		AssertVector3(
			DdgiUtilities.GetRuntimeOrigin(anchor, shape, spacing, initialCenter + new Vector3(0.99f, 0.0f, 0.0f)),
			anchor);
		AssertVector3(
			DdgiUtilities.GetRuntimeOrigin(anchor, shape, spacing, initialCenter + new Vector3(1.01f, 0.0f, 0.0f)),
			anchor + new Vector3(spacing, 0.0f, 0.0f));
	}

	[Test]
	public void DdgiRuntimeOriginSnapsOnPositiveNegativeAndMultipleAxes()
	{
		var shape = new DdgiGridShape(3, 3, 3, 27, 6, 5);
		var anchor = new Vector3(10.0f, -2.0f, 4.0f);
		const float spacing = 2.0f;
		var initialCenter = anchor + new Vector3(spacing);
		var camera = initialCenter + new Vector3(4.1f, -2.1f, 1.1f);

		var origin = DdgiUtilities.GetRuntimeOrigin(anchor, shape, spacing, camera);

		AssertVector3(origin, anchor + new Vector3(4.0f, 0.0f, 2.0f));
	}

	[Test]
	public void DdgiRuntimeOriginKeepsAuthoredVerticalAnchor()
	{
		var shape = new DdgiGridShape(40, 20, 40, 32000, 179, 179);
		var anchor = Vector3.Zero;
		const float spacing = 2.0f;
		var camera = new Vector3(39.0f, 1.5f, 39.0f);

		var origin = DdgiUtilities.GetRuntimeOrigin(anchor, shape, spacing, camera);

		Assert.That(origin.Y, Is.EqualTo(anchor.Y));
	}

	[Test]
	public void DdgiCircularStoragePreservesWorldProbeAcrossScroll()
	{
		var shape = new DdgiGridShape(4, 3, 5, 60, 8, 8);
		var previousOffset = new Int3(3, 1, 4);
		var scrollDelta = new Int3(1, -1, 2);
		var currentOffset = DdgiUtilities.AdvanceStorageOffset(previousOffset, scrollDelta, shape);
		var currentLogicalIndex = 1 + shape.CountX + shape.CountX * shape.CountY;
		var previousLogicalIndex = 2 + 3 * shape.CountX * shape.CountY;

		Assert.That(
			DdgiUtilities.GetPhysicalProbeIndex(currentLogicalIndex, currentOffset, shape),
			Is.EqualTo(DdgiUtilities.GetPhysicalProbeIndex(previousLogicalIndex, previousOffset, shape)));
	}

	[Test]
	public void DdgiCircularStorageWrapsNegativeOffsets()
	{
		var shape = new DdgiGridShape(4, 3, 2, 24, 5, 5);

		var offset = DdgiUtilities.AdvanceStorageOffset(
			new Int3(0, 0, 0),
			new Int3(-1, -4, -3),
			shape);

		Assert.That(offset, Is.EqualTo(new Int3(3, 2, 1)));
	}

	[Test]
	public void DdgiNewlyExposedProbeClassificationMatchesScrolledSlab()
	{
		var shape = new DdgiGridShape(4, 3, 2, 24, 5, 5);
		var delta = new Int3(1, 0, 0);

		Assert.That(DdgiUtilities.GetNewlyExposedProbeCount(delta, shape), Is.EqualTo(6));
		for (var probeIndex = 0; probeIndex < shape.ProbeCount; probeIndex++)
		{
			var coord = DdgiUtilities.GetLogicalProbeCoord(probeIndex, shape);
			Assert.That(DdgiUtilities.IsProbeNewlyExposed(probeIndex, delta, shape), Is.EqualTo(coord.X == 3));
		}
	}

	[Test]
	public void DdgiTeleportBeyondVolumeExposesEveryProbe()
	{
		var shape = new DdgiGridShape(4, 3, 2, 24, 5, 5);

		Assert.That(
			DdgiUtilities.GetNewlyExposedProbeCount(new Int3(-4, 0, 0), shape),
			Is.EqualTo(shape.ProbeCount));
	}

	[Test]
	public void DdgiActiveProbeCountIncludesExposedProbesWithoutDoubleCounting()
	{
		var shape = new DdgiGridShape(4, 2, 2, 16, 4, 4);

		var count = DdgiUtilities.GetActiveProbeCount(
			shape,
			probeUpdateFrames: 4,
			probeUpdateFrameIndex: 3,
			forceFullUpdate: false,
			scrollDelta: new Int3(1, 0, 0),
			historyValid: true);

		Assert.That(count, Is.EqualTo(4));
	}

	[Test]
	public void DdgiIrradianceEstimatorUsesPackedDirectionStatePerProbe()
	{
		var shape = DdgiUtilities.GetGridShape(new DiffuseGlobalIlluminationConfig
		{
			ProbeCounts = new DdgiProbeCounts { X = 8, Y = 8, Z = 8 }
		});

		Assert.That(DdgiUtilities.IrradianceEstimatorDirectionCount, Is.EqualTo(64));
		Assert.That(DdgiUtilities.IrradianceEstimatorStride, Is.EqualTo(16));
		Assert.That(
			DdgiUtilities.GetIrradianceEstimatorBufferSize(shape),
			Is.EqualTo((ulong)shape.ProbeCount * 64UL * 16UL));
	}

	[Test]
	public void RenderGraphRegistryMaterializesImportedBuffersAsGenericResources()
	{
		var registry = new RenderGraphResourceRegistry();
		var buffer = new TestBuffer(BufferUsage.Structured);
		var handle = registry.ImportBuffer(buffer, initialState: ResourceState.UnorderedAccess);

		Assert.That(registry.GetResource(handle), Is.SameAs(buffer));
		Assert.That(registry.GetResourceState(handle), Is.EqualTo(ResourceState.UnorderedAccess));
	}

	[TestCase(0.0f, 0.0f, 0.0f)]
	[TestCase(1.0f, 0.25f, 8.0f)]
	[TestCase(120.0f, 4.0f, 0.5f)]
	public void DdgiEstimatorPackingRoundTripsHdrValues(float r, float g, float b)
	{
		var value = new Vector3(r, g, b);
		var unpacked = DdgiUtilities.UnpackRgbe(DdgiUtilities.PackRgbe(value));
		var tolerance = MathF.Max(0.002f, MathF.Max(r, MathF.Max(g, b)) / 256.0f);

		Assert.That(unpacked.X, Is.EqualTo(r).Within(tolerance));
		Assert.That(unpacked.Y, Is.EqualTo(g).Within(tolerance));
		Assert.That(unpacked.Z, Is.EqualTo(b).Within(tolerance));

		var halfValues = new Vector2(0.03125f + r * 0.001f, 1.0f + g * 0.001f);
		var unpackedHalf = DdgiUtilities.UnpackHalf2(DdgiUtilities.PackHalf2(halfValues));
		Assert.That(unpackedHalf.X, Is.EqualTo(halfValues.X).Within(0.002f));
		Assert.That(unpackedHalf.Y, Is.EqualTo(halfValues.Y).Within(0.002f));
	}

	[Test]
	public void DdgiEstimatorSuppressesSingleFireflyAndConvergesAfterLightingChange()
	{
		var stable = new Vector3(1.0f);
		var state = new DdgiVarianceData(stable, stable, 0.0f, Vector3.Zero, 1.0f);
		state = DdgiUtilities.UpdateVarianceEstimator(stable, state, 0.08f);
		var beforeFirefly = state.Mean;
		state = DdgiUtilities.UpdateVarianceEstimator(new Vector3(1000.0f), state, 0.08f);
		Assert.That(state.Mean.X - beforeFirefly.X, Is.LessThan(0.1f));

		for (var i = 0; i < 96; i++)
		{
			state = DdgiUtilities.UpdateVarianceEstimator(new Vector3(4.0f), state, 0.08f);
		}

		Assert.That(state.Mean.X, Is.GreaterThan(2.5f));
		Assert.That(state.Mean.X, Is.LessThanOrEqualTo(4.0f));
	}

	[Test]
	public void DdgiEstimatorCapsAdaptiveCatchUpAtTwoPercentPerUpdate()
	{
		var state = new DdgiVarianceData(
			Vector3.Zero,
			new Vector3(10.0f),
			1.0f,
			new Vector3(100.0f),
			10.0f);

		var updated = DdgiUtilities.UpdateVarianceEstimator(new Vector3(10.0f), state, 0.08f);

		Assert.That(updated.Mean.X, Is.EqualTo(0.2f).Within(1e-5f));
		Assert.That(updated.Mean.Y, Is.EqualTo(0.2f).Within(1e-5f));
		Assert.That(updated.Mean.Z, Is.EqualTo(0.2f).Within(1e-5f));
	}

	[Test]
	public void DdgiEstimatorAlternatingNoiseCannotCauseLargeMeanJumps()
	{
		var state = new DdgiVarianceData(
			new Vector3(5.0f),
			new Vector3(5.0f),
			1.0f,
			new Vector3(25.0f),
			10.0f);

		for (var updateIndex = 0; updateIndex < 32; updateIndex++)
		{
			var sample = new Vector3(updateIndex % 2 == 0 ? 0.0f : 10.0f);
			var previousMean = state.Mean;
			state = DdgiUtilities.UpdateVarianceEstimator(sample, state, 0.08f);

			var maximumAllowedMovement = Vector3.Abs(sample - previousMean) * 0.02f;
			var actualMovement = Vector3.Abs(state.Mean - previousMean);
			Assert.That(actualMovement.X, Is.LessThanOrEqualTo(maximumAllowedMovement.X + 1e-5f));
			Assert.That(actualMovement.Y, Is.LessThanOrEqualTo(maximumAllowedMovement.Y + 1e-5f));
			Assert.That(actualMovement.Z, Is.LessThanOrEqualTo(maximumAllowedMovement.Z + 1e-5f));
		}
	}

	[Test]
	public void DdgiL1ShReconstructsConstantRadianceForEveryNormal()
	{
		const int sampleCount = 4096;
		var radiance = new Vector3(1.5f, 0.75f, 0.25f);
		var sh = default(DdgiL1Sh);
		var solidAngle = 4.0f * MathF.PI / sampleCount;
		for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
		{
			sh += DdgiUtilities.ProjectRadiance(SphericalFibonacci(sampleIndex, sampleCount), radiance, solidAngle);
		}

		foreach (var normal in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(Vector3.One) })
		{
			var evaluated = DdgiUtilities.EvaluateDiffuse(sh, normal);
			Assert.That(evaluated.X, Is.EqualTo(radiance.X).Within(0.002f));
			Assert.That(evaluated.Y, Is.EqualTo(radiance.Y).Within(0.002f));
			Assert.That(evaluated.Z, Is.EqualTo(radiance.Z).Within(0.002f));
		}
	}

	[Test]
	public void DdgiL1ShDirectionalCoefficientsFollowAxisAndEvaluateSmoothly()
	{
		var radiance = Vector3.One;
		var xSh = DdgiUtilities.ProjectRadiance(Vector3.UnitX, radiance, 1.0f);
		var ySh = DdgiUtilities.ProjectRadiance(Vector3.UnitY, radiance, 1.0f);
		var zSh = DdgiUtilities.ProjectRadiance(Vector3.UnitZ, radiance, 1.0f);

		Assert.That(xSh.Lx.X, Is.GreaterThan(0.0f));
		Assert.That(xSh.Ly, Is.EqualTo(Vector3.Zero));
		Assert.That(xSh.Lz, Is.EqualTo(Vector3.Zero));
		Assert.That(ySh.Ly.X, Is.GreaterThan(0.0f));
		Assert.That(ySh.Lx, Is.EqualTo(Vector3.Zero));
		Assert.That(ySh.Lz, Is.EqualTo(Vector3.Zero));
		Assert.That(zSh.Lz.X, Is.GreaterThan(0.0f));
		Assert.That(zSh.Lx, Is.EqualTo(Vector3.Zero));
		Assert.That(zSh.Ly, Is.EqualTo(Vector3.Zero));

		var facing = DdgiUtilities.EvaluateDiffuse(xSh, Vector3.UnitX).X;
		var perpendicular = DdgiUtilities.EvaluateDiffuse(xSh, Vector3.UnitY).X;
		var halfway = DdgiUtilities.EvaluateDiffuse(xSh, Vector3.Normalize(Vector3.UnitX + Vector3.UnitY)).X;
		var opposite = DdgiUtilities.EvaluateDiffuse(xSh, -Vector3.UnitX);
		Assert.That(halfway, Is.GreaterThan(perpendicular));
		Assert.That(halfway, Is.LessThan(facing));
		Assert.That(opposite.X, Is.GreaterThan(0.0f));
		Assert.That(opposite.Y, Is.GreaterThan(0.0f));
		Assert.That(opposite.Z, Is.GreaterThan(0.0f));
		Assert.That(opposite.X, Is.EqualTo(opposite.Y).Within(1e-6f));
		Assert.That(opposite.Y, Is.EqualTo(opposite.Z).Within(1e-6f));
	}

	[Test]
	public void DdgiL1ShReconstructsConstantRawRadianceForEveryDirection()
	{
		const int sampleCount = 4096;
		var radiance = new Vector3(1.5f, 0.75f, 0.25f);
		var sh = default(DdgiL1Sh);
		var solidAngle = 4.0f * MathF.PI / sampleCount;
		for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
		{
			sh += DdgiUtilities.ProjectRadiance(SphericalFibonacci(sampleIndex, sampleCount), radiance, solidAngle);
		}

		foreach (var direction in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(Vector3.One) })
		{
			AssertVector3(DdgiUtilities.EvaluateRadiance(sh, direction), radiance, 0.002f);
		}
	}

	[Test]
	public void DdgiRawRadianceFollowsDirectionalCoefficientAxis()
	{
		var sh = DdgiUtilities.ProjectRadiance(Vector3.UnitX, Vector3.One, 1.0f);

		var facing = DdgiUtilities.EvaluateRadiance(sh, Vector3.UnitX).X;
		var perpendicular = DdgiUtilities.EvaluateRadiance(sh, Vector3.UnitY).X;
		var opposite = DdgiUtilities.EvaluateRadiance(sh, -Vector3.UnitX).X;

		Assert.That(facing, Is.GreaterThan(perpendicular));
		Assert.That(perpendicular, Is.GreaterThan(opposite));
	}

	[Test]
	public void DdgiRawRadianceClampsDirectionalLobeBeforeItBecomesNegative()
	{
		var sh = new DdgiL1Sh(
			new Vector3(1.0f),
			Vector3.Zero,
			Vector3.Zero,
			new Vector3(100.0f));

		var opposite = DdgiUtilities.EvaluateRadiance(sh, -Vector3.UnitX);

		Assert.That(opposite.X, Is.GreaterThan(0.0f));
		Assert.That(opposite.Y, Is.GreaterThan(0.0f));
		Assert.That(opposite.Z, Is.GreaterThan(0.0f));
	}

	[Test]
	public void DdgiRoughSpecularBlendUsesRoughnessVolumeAndSampleValidity()
	{
		Assert.That(DdgiUtilities.GetRoughSpecularBlend(1.0f, 0.25f, true), Is.EqualTo(0.0f));
		Assert.That(DdgiUtilities.GetRoughSpecularBlend(1.0f, 0.6f, true), Is.EqualTo(1.0f));
		Assert.That(DdgiUtilities.GetRoughSpecularBlend(0.4f, 0.6f, true), Is.EqualTo(0.4f).Within(1e-6f));
		Assert.That(DdgiUtilities.GetRoughSpecularBlend(1.0f, 1.0f, false), Is.EqualTo(0.0f));
		Assert.That(DdgiUtilities.GetRoughSpecularBlend(0.0f, 1.0f, true), Is.EqualTo(0.0f));
	}

	[Test]
	public void DdgiVisibilityMomentsRemainDirectional()
	{
		Assert.That(DdgiUtilities.GetVisibilityDirectionalWeight(1.0f), Is.EqualTo(1.0f));
		Assert.That(DdgiUtilities.GetVisibilityDirectionalWeight(0.9f), Is.LessThan(0.04f));
		Assert.That(DdgiUtilities.GetVisibilityDirectionalWeight(0.5f), Is.LessThan(1e-8f));
		Assert.That(DdgiUtilities.GetVisibilityDirectionalWeight(0.9f, 64), Is.GreaterThan(0.3f));
	}

	[Test]
	public void DdgiOctahedralProjectionWeightsDistortedDirectionsBySolidAngle()
	{
		var centerWeight = DdgiUtilities.GetOctahedralSolidAngleWeight(Vector2.Zero);
		var diagonalWeight = DdgiUtilities.GetOctahedralSolidAngleWeight(new Vector2(0.5f, 0.5f));

		Assert.That(centerWeight, Is.EqualTo(1.0f).Within(1e-6f));
		Assert.That(diagonalWeight, Is.GreaterThan(centerWeight));
	}

	[Test]
	public void DdgiVisibilityStronglyRejectsSamplesBehindOccluders()
	{
		const float meanDistance = 0.25f;
		const float meanDistanceSquared = 0.065f;

		Assert.That(
			DdgiUtilities.EvaluateVisibility(meanDistance, meanDistanceSquared, 0.2f),
			Is.EqualTo(1.0f));
		Assert.That(
			DdgiUtilities.EvaluateVisibility(meanDistance, meanDistanceSquared, 0.5f),
			Is.LessThan(0.001f));
	}

	[Test]
	public void DdgiVisibilityRejectsNearReceiversBehindLowVarianceOccluders()
	{
		const float meanDistance = 0.25f;
		const float meanDistanceSquared = meanDistance * meanDistance;

		Assert.That(
			DdgiUtilities.EvaluateVisibility(meanDistance, meanDistanceSquared, 0.26f),
			Is.LessThan(0.001f));
	}

	[Test]
	public void DdgiDiffuseHitUsesLambertianDirectNormalization()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			new Vector3(MathF.PI),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: Vector3.Zero,
			emissive: Vector3.Zero,
			historyValid: false);

		AssertVector3(shaded, Vector3.One);
	}

	[Test]
	public void DdgiDiffuseHitPreservesWhiteSurfaceColorRatios()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			new Vector3(1.0f, 2.0f, 4.0f),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: Vector3.Zero,
			emissive: Vector3.Zero,
			historyValid: false);

		Assert.That(shaded.Y / shaded.X, Is.EqualTo(2.0f).Within(1e-6f));
		Assert.That(shaded.Z / shaded.X, Is.EqualTo(4.0f).Within(1e-6f));
	}

	[Test]
	public void DdgiDiffuseHitAlbedoTintsDirectAndRecursiveRadiance()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			new Vector3(1.0f, 0.25f, 0.0f),
			new Vector3(MathF.PI),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: Vector3.One,
			emissive: Vector3.Zero,
			historyValid: true);

		var expectedX = 1.0f + DdgiUtilities.DefaultRecursiveBounceEnergy;
		Assert.That(shaded.X, Is.EqualTo(expectedX).Within(1e-6f));
		Assert.That(shaded.Y, Is.EqualTo(expectedX * 0.25f).Within(1e-6f));
		Assert.That(shaded.Z, Is.EqualTo(0.0f).Within(1e-6f));
	}

	[Test]
	public void DdgiDiffuseHitAddsEmissiveIndependentlyOfAlbedo()
	{
		var emissive = new Vector3(3.0f, 1.0f, 0.5f);
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.Zero,
			new Vector3(100.0f),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: new Vector3(100.0f),
			emissive,
			historyValid: true);

		AssertVector3(shaded, emissive);
	}

	[Test]
	public void DdgiDiffuseHitIgnoresRecursiveRadianceWithoutHistory()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			Vector3.Zero,
			normalDotLight: 0.0f,
			visibility: 0.0f,
			previousDdgi: new Vector3(10.0f),
			emissive: Vector3.Zero,
			historyValid: false);

		AssertVector3(shaded, Vector3.Zero);
	}

	[Test]
	public void DdgiDiffuseHitDoesNotNormalizeAlreadyConvolvedHistoryTwice()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			Vector3.Zero,
			normalDotLight: 0.0f,
			visibility: 0.0f,
			previousDdgi: Vector3.One,
			emissive: Vector3.Zero,
			historyValid: true);

		AssertVector3(shaded, new Vector3(DdgiUtilities.DefaultRecursiveBounceEnergy));
	}

	[Test]
	public void DdgiRecursiveBounceEnergyClampsToPhysicalRange()
	{
		Assert.That(
			DdgiUtilities.GetRecursiveBounceEnergy(new DiffuseGlobalIlluminationConfig
			{
				RecursiveBounceEnergy = -1.0f
			}),
			Is.EqualTo(0.0f));
		Assert.That(
			DdgiUtilities.GetRecursiveBounceEnergy(new DiffuseGlobalIlluminationConfig
			{
				RecursiveBounceEnergy = 2.0f
			}),
			Is.EqualTo(1.0f));
	}

	[Test]
	public void DdgiRelocationDirectionsAreDeterministicNormalizedAndBalanced()
	{
		var sum = Vector3.Zero;
		var directions = new HashSet<Vector3>();
		for (var index = 0; index < DdgiUtilities.RelocationRayCount; index++)
		{
			var first = DdgiUtilities.GetRelocationRayDirection(index);
			var second = DdgiUtilities.GetRelocationRayDirection(index);
			AssertVector3(first, second);
			Assert.That(first.Length(), Is.EqualTo(1.0f).Within(1e-5f));
			Assert.That(directions.Add(first), Is.True);
			sum += first;
		}

		Assert.That(sum.Length(), Is.LessThan(0.2f));
	}

	[Test]
	public void DdgiRelocationBackfaceThresholdIsStrict()
	{
		var hits = CreateRelocationMisses();
		for (var index = 0; index < 4; index++)
		{
			hits[index] = new DdgiRelocationHit(Vector3.UnitY, 0.1f, Backface: true);
		}

		var boundary = DdgiUtilities.SolveProbeRelocation(hits, 0.2f, 1.0f, 10.0f);
		hits[4] = new DdgiRelocationHit(Vector3.UnitY, 0.1f, Backface: true);
		var above = DdgiUtilities.SolveProbeRelocation(hits, 0.2f, 1.0f, 10.0f);

		Assert.That(boundary.State, Is.EqualTo(DdgiProbeState.Stable));
		Assert.That(above.State, Is.EqualTo(DdgiProbeState.Stable));
		Assert.That(above.Decision, Is.EqualTo(DdgiProbeRelocationDecision.BackfaceEscape));
	}

	[Test]
	public void DdgiRelocationEscapesClosestBackfaceImmediately()
	{
		var hits = CreateRelocationMisses();
		for (var index = 0; index < 5; index++)
		{
			hits[index] = new DdgiRelocationHit(Vector3.UnitY, index == 0 ? 0.1f : 0.4f, Backface: true);
		}

		var result = DdgiUtilities.SolveProbeRelocation(
			hits,
			keepDistance: 0.2f,
			maxRelocationDistance: 1.0f,
			maxRayDistance: 10.0f,
			previousOffset: new Vector3(0.1f, 0.0f, 0.0f));

		AssertVector3(result.Offset, new Vector3(0.1f, 0.2f, 0.0f));
		Assert.That(result.State, Is.EqualTo(DdgiProbeState.Stable));
	}

	[Test]
	public void DdgiRelocationReturnsToGridWhenClear()
	{
		var hits = CreateRelocationMisses();
		for (var index = 0; index < 5; index++)
		{
			hits[index] = new DdgiRelocationHit(Vector3.UnitY, 0.1f, Backface: true);
		}

		var escaped = DdgiUtilities.SolveProbeRelocation(hits, 0.2f, 1.0f, 10.0f);
		var settled = DdgiUtilities.SolveProbeRelocation(
			CreateRelocationMisses(),
			keepDistance: 0.2f,
			maxRelocationDistance: 1.0f,
			maxRayDistance: 10.0f,
			previousOffset: escaped.Offset);

		AssertVector3(settled.Offset, Vector3.Zero);
		Assert.That(settled.State, Is.EqualTo(DdgiProbeState.Stable));
		Assert.That(settled.Decision, Is.EqualTo(DdgiProbeRelocationDecision.ReturnToLattice));
	}

	[Test]
	public void DdgiRelocationMovesTowardFarthestFrontface()
	{
		var hits = new[]
		{
			new DdgiRelocationHit(Vector3.UnitX, 0.05f),
			new DdgiRelocationHit(-Vector3.UnitX, 0.4f),
			new DdgiRelocationHit(Vector3.Normalize(new Vector3(-0.2f, 1.0f, 0.0f)), 5.0f)
		};
		var result = DdgiUtilities.SolveProbeRelocation(
			hits,
			keepDistance: 0.2f,
			maxRelocationDistance: 2.0f,
			maxRayDistance: 10.0f);

		AssertVector3(
			result.Offset,
			Vector3.Normalize(new Vector3(-0.2f, 1.0f, 0.0f)));
		Assert.That(result.State, Is.EqualTo(DdgiProbeState.Stable));
		Assert.That(result.Decision, Is.EqualTo(DdgiProbeRelocationDecision.FrontfaceSeparation));
	}

	[Test]
	public void DdgiRelocationBackfaceEscapeIncludesHalfFrontfaceMargin()
	{
		var hits = CreateRelocationMisses();
		for (var index = 0; index < 5; index++)
		{
			hits[index] = new DdgiRelocationHit(Vector3.UnitY, 0.1f, Backface: true);
		}

		var withoutHint = DdgiUtilities.SolveProbeRelocation(
			hits,
			keepDistance: 0.0f,
			maxRelocationDistance: 2.0f,
			maxRayDistance: 10.0f);
		var withHint = DdgiUtilities.SolveProbeRelocation(
			hits,
			keepDistance: 1.0f,
			maxRelocationDistance: 2.0f,
			maxRayDistance: 10.0f);

		AssertVector3(withoutHint.Offset, new Vector3(0.0f, 0.1f, 0.0f));
		AssertVector3(withHint.Offset, new Vector3(0.0f, 0.6f, 0.0f));
	}

	[Test]
	public void DdgiRelocationStableProbeAppliesFrontfaceHintWithoutBlocking()
	{
		var previousOffset = new Vector3(0.4f, 0.2f, -0.1f);
		var result = DdgiUtilities.SolveProbeRelocation(
			[
				new DdgiRelocationHit(Vector3.UnitX, 0.01f),
				new DdgiRelocationHit(-Vector3.UnitX, 1.0f)
			],
			keepDistance: 0.5f,
			maxRelocationDistance: 1.0f,
			maxRayDistance: 10.0f,
			previousOffset: previousOffset);

		AssertVector3(result.Offset, previousOffset - Vector3.UnitX);
		Assert.That(result.State, Is.EqualTo(DdgiProbeState.Stable));
		Assert.That(result.Decision, Is.EqualTo(DdgiProbeRelocationDecision.FrontfaceSeparation));
	}

	[Test]
	public void DdgiRelocationRepeatedStableRevalidationReturnsOffsetToGrid()
	{
		var originalOffset = new Vector3(0.4f, -0.2f, 0.1f);
		var offset = originalOffset;
		for (var revalidation = 0; revalidation < 64; revalidation++)
		{
			var result = DdgiUtilities.SolveProbeRelocation(
				CreateRelocationMisses(),
				keepDistance: 0.2f,
				maxRelocationDistance: 1.0f,
				maxRayDistance: 10.0f,
				previousOffset: offset);

			offset = result.Offset;
			Assert.That(result.State, Is.EqualTo(DdgiProbeState.Stable));
			Assert.That(
				result.Decision,
				Is.AnyOf(DdgiProbeRelocationDecision.ReturnToLattice, DdgiProbeRelocationDecision.None));
		}

		AssertVector3(offset, Vector3.Zero);
	}

	[Test]
	public void DdgiRelocationIgnoresSubQuantizationFrontfaceCorrection()
	{
		var hits = CreateRelocationMisses();
		hits[0] = new DdgiRelocationHit(Vector3.UnitX, 0.199f);
		var previousOffset = new Vector3(1.5f, 0.0f, 0.0f);

		var result = DdgiUtilities.SolveProbeRelocation(
			hits,
			keepDistance: 0.2f,
			maxRelocationDistance: 2.0f,
			maxRayDistance: 10.0f,
			previousOffset: previousOffset);

		AssertVector3(result.Offset, previousOffset);
		Assert.That(result.State, Is.EqualTo(DdgiProbeState.Stable));
		Assert.That(result.Decision, Is.EqualTo(DdgiProbeRelocationDecision.None));
	}

	[Test]
	public void DdgiBackfaceHitHelperClassifiesRayBehindSurfaceNormal()
	{
		Assert.That(DdgiUtilities.IsBackfaceHit(Vector3.UnitY, Vector3.UnitY), Is.True);
		Assert.That(DdgiUtilities.IsBackfaceHit(Vector3.UnitY, -Vector3.UnitY), Is.False);
	}

	[Test]
	public void DdgiRelocationFrontfaceHintDoesNotBlockWithoutClearance()
	{
		var result = DdgiUtilities.SolveProbeRelocation(
			[
				new DdgiRelocationHit(Vector3.UnitX, 0.05f),
				new DdgiRelocationHit(-Vector3.UnitX, 0.2f)
			],
			keepDistance: 0.2f,
			maxRelocationDistance: 1.0f,
			maxRayDistance: 0.2f);

		AssertVector3(result.Offset, -Vector3.UnitX * 0.2f);
		Assert.That(result.State, Is.EqualTo(DdgiProbeState.Stable));
		Assert.That(result.Decision, Is.EqualTo(DdgiProbeRelocationDecision.FrontfaceSeparation));
	}

	[Test]
	public void DdgiRelocationUsesRadialDistanceLimit()
	{
		var clamped = DdgiUtilities.ClampProbeRelocationOffset(new Vector3(1.0f, 1.0f, 1.0f), 0.25f);

		Assert.That(clamped.Length(), Is.EqualTo(0.25f).Within(1e-6f));
		AssertVector3(Vector3.Normalize(clamped), Vector3.Normalize(Vector3.One));
	}

	[Test]
	public void DdgiRelocationRejectsCandidateOutsideDistanceLimitWithoutBlocking()
	{
		var hits = CreateRelocationMisses();
		for (var index = 0; index < 5; index++)
		{
			hits[index] = new DdgiRelocationHit(Vector3.UnitX, 0.1f, Backface: true);
		}

		var result = DdgiUtilities.SolveProbeRelocation(
			hits,
			keepDistance: 0.2f,
			maxRelocationDistance: 0.5f,
			maxRayDistance: 10.0f,
			previousOffset: new Vector3(0.5f, 0.0f, 0.0f));

		AssertVector3(result.Offset, new Vector3(0.5f, 0.0f, 0.0f));
		Assert.That(result.State, Is.EqualTo(DdgiProbeState.Stable));
	}

	[Test]
	public void DdgiRelocationEscapesFloorThenWallWithinTwoIterations()
	{
		var floorHits = CreateRelocationMisses();
		var wallHits = CreateRelocationMisses();
		for (var index = 0; index < 5; index++)
		{
			floorHits[index] = new DdgiRelocationHit(Vector3.UnitY, 0.1f, Backface: true);
			wallHits[index] = new DdgiRelocationHit(Vector3.UnitX, 0.15f, Backface: true);
		}

		var floorResult = DdgiUtilities.SolveProbeRelocation(floorHits, 0.2f, 2.0f, 10.0f);
		var wallResult = DdgiUtilities.SolveProbeRelocation(
			wallHits,
			0.2f,
			2.0f,
			10.0f,
			floorResult.Offset);

		Assert.That(floorResult.Offset.Y, Is.GreaterThan(0.0f));
		Assert.That(wallResult.Offset.Y, Is.EqualTo(floorResult.Offset.Y).Within(1e-6f));
		Assert.That(wallResult.Offset.X, Is.GreaterThan(0.0f));
	}

	[Test]
	public void DdgiRelocationSchedulingUsesHistoryAndFrameSchedule()
	{
		Assert.That(
			DdgiUtilities.IsProbeRelocationUpdateActive(
				enabled: true, hasHistory: true, scheduled: false),
			Is.False);
		Assert.That(
			DdgiUtilities.IsProbeRelocationUpdateActive(
				enabled: true, hasHistory: true, scheduled: true),
			Is.True);
		Assert.That(DdgiUtilities.CanProbeContribute(DdgiProbeState.Stable, enabled: true), Is.True);
		Assert.That(DdgiUtilities.CanProbeContribute(DdgiProbeState.Disabled, enabled: true), Is.False);
	}

	[Test]
	public void DdgiRelocationUsesSinglePass()
	{
		Assert.That(DdgiUtilities.RelocationIterationCount, Is.EqualTo(1));
	}

	[Test]
	public void DdgiRelocationDistanceFactorClampsToSafeGridLimit()
	{
		var distance = DdgiUtilities.GetProbeMaxRelocationDistance(new DiffuseGlobalIlluminationConfig
		{
			ProbeSpacing = 2.0f,
			ProbeMaxRelocationDistanceFactor = 1.0f
		});

		Assert.That(distance, Is.EqualTo(0.9f));
	}

	[Test]
	public void DdgiRelocationDistanceFactorClampsAboveSafeGridLimit()
	{
		var distance = DdgiUtilities.GetProbeMaxRelocationDistance(new DiffuseGlobalIlluminationConfig
		{
			ProbeSpacing = 2.0f,
			ProbeMaxRelocationDistanceFactor = 4.0f
		});

		Assert.That(distance, Is.EqualTo(0.9f));
	}

	[Test]
	public void DdgiRelocationInactiveProbePreservesPreviousOffset()
	{
		var previous = new Vector3(0.2f, -0.1f, 0.05f);
		var updated = DdgiUtilities.UpdateProbeRelocation(
			previous,
			new Vector3(-0.9f),
			maxRelocationDistance: 0.9f,
			active: false);

		Assert.That(updated, Is.EqualTo(previous));
	}

	[Test]
	public void RenderPipeline_DdgiProbeDebugToggleInjectsSpherePrimitives()
	{
		var config = new RenderConfig
		{
			DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
			{
				Enabled = true,
				Mode = DiffuseGlobalIlluminationMode.RayTracedDdgi,
				ProbeCounts = new DdgiProbeCounts { X = 2, Y = 1, Z = 2 },
				ProbeSpacing = 3.0f,
				DebugProbeSpheres = true,
				DebugProbeSphereRadius = 0.25f
			}
		};
		var database = new GpuDrawDatabase();
		var meshFactory = new DebugPrimitiveMeshFactory();
		var cameraPosition = new Vector3(4.6f, 0.0f, 1.5f);

		database.BeginSync();
		RenderPipeline.CollectDdgiProbeDebugPrimitives(config, cameraPosition, database, meshFactory);
		database.EndSync();

		var entries = new List<GpuDrawEntry>();
		database.CollectDrawEntries(entries);

		Assert.That(entries, Has.Count.EqualTo(4));
		Assert.That(entries.Select(entry => entry.DrawKind), Is.All.EqualTo(GpuDrawKind.DebugPrimitive));
		Assert.That(entries.Select(entry => entry.Material.AlphaMode), Is.All.EqualTo(AlphaMode.AlphaBlend));
		Assert.That(entries.Select(entry => entry.Material.Color), Is.All.EqualTo(ColorRGBA.White));
		Assert.That(
			entries.Select(entry => entry.TerrainInstanceData.ChunkOriginSize.X),
			Is.EquivalentTo(new[] { 0.0f, 1.0f, 2.0f, 3.0f }));
		Assert.That(
			entries.Select(entry => entry.TerrainInstanceData.ChunkOriginSize.Y),
			Is.All.EqualTo(1.0f));
		Assert.That(entries.Select(entry => entry.World.M11), Is.All.EqualTo(0.5f).Within(0.0001f));
		Assert.That(entries.Select(entry => entry.World.M41).Distinct(), Is.EquivalentTo(new[] { 3.0f, 6.0f }));
		Assert.That(entries.Select(entry => entry.World.M43).Distinct(), Is.EquivalentTo(new[] { 0.0f, 3.0f }));
	}

	[Test]
	public void RecordUpdate_BootstrapBuildsOpaqueMeshAndTerrainSceneAndReportsSkippedDraws()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Terrain ray tracing BLAS tests only run on macOS.");
		}

		var database = new GpuDrawDatabase();
		var opaqueMesh = CreateTestMesh();
		var alphaMesh = CreateTestMesh();
		var terrainMesh = CreateTestMesh();
		var opaqueMaterial = new Material("opaque");
		var alphaMaterial = new Material("alpha") { AlphaMode = AlphaMode.AlphaTest };
		var terrainMaterial = new Material("__terrain__");
		database.BeginSync();
		database.TouchMesh(new Entity(1, 1), opaqueMesh, opaqueMaterial, Matrix4x4.Identity);
		database.TouchMesh(new Entity(2, 1), alphaMesh, alphaMaterial, Matrix4x4.Identity);
		database.TouchTerrainChunk(
			new Entity(3, 1),
			0,
			terrainMesh,
			terrainMaterial,
			terrainMesh.BoundingSphere,
			CreateTerrainInstanceData(),
			CreateTerrainSurface(),
			Matrix4x4.Identity);
		database.EndSync();
		var updates = new List<GpuDrawUpdate>();
		database.CopyUpdates(updates);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources(new ShaderCompiler());

		resources.RecordUpdate(context, new TestRenderer(new TestDevice()), updates);

		Assert.That(resources.LastStats.BottomLevelAccelerationStructureCount, Is.EqualTo(2));
		Assert.That(resources.LastStats.TopLevelInstanceCount, Is.EqualTo(2));
		Assert.That(resources.LastStats.PendingBottomLevelBuildCount, Is.EqualTo(2));
		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.Bootstrap));
		Assert.That(resources.LastStats.SkippedTerrainCount, Is.EqualTo(0));
		Assert.That(resources.LastStats.SkippedTransparentOrAlphaCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.SidecarHitShadingAvailable, Is.True);
		Assert.That(resources.InstanceIndexToInstanceHandleBuffer, Is.Not.Null);
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(2));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(1));
		var terrainBuild = commandList.BottomLevelBuilds.Single(build =>
			(build.Descriptor.VertexBuffer.Descriptor.Usage & BufferUsage.Structured) != 0);
		Assert.That(terrainBuild.Descriptor.VertexStrideBytes, Is.EqualTo(12));
		Assert.That(terrainBuild.Descriptor.VertexBuffer.Descriptor.SizeInBytes, Is.EqualTo(17UL * 17UL * 12UL));
	}

	[Test]
	public void RecordUpdate_MaterialOnlyUpdateDoesNotRebuildTlas()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateTestMesh();
		var materialA = new Material("a");
		var materialB = new Material("b");
		var entity = new Entity(1, 1);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources(new ShaderCompiler());
		var renderer = new TestRenderer(new TestDevice());

		database.BeginSync();
		database.TouchMesh(entity, mesh, materialA, Matrix4x4.Identity);
		database.EndSync();
		var updates = new List<GpuDrawUpdate>();
		database.CopyUpdates(updates);
		resources.RecordUpdate(context, renderer, updates);
		database.ConsumeUpdates(updates);

		database.BeginSync();
		database.TouchMesh(entity, mesh, materialB, Matrix4x4.Identity);
		database.EndSync();
		database.CopyUpdates(updates);
		commandList.ResetCounts();

		resources.RecordUpdate(context, renderer, updates);

		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(0));
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.None));
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(0));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(0));
	}

	[Test]
	public void RecordUpdate_TerrainGeometryRevisionRebuildsBlasWithoutRebuildingTlas()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Terrain ray tracing BLAS tests only run on macOS.");
		}

		var database = new GpuDrawDatabase();
		var terrainMesh = CreateTestMesh();
		var terrainMaterial = new Material("__terrain__");
		var entity = new Entity(1, 1);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources(new ShaderCompiler());
		var renderer = new TestRenderer(new TestDevice());
		var instanceData = CreateTerrainInstanceData();
		var surface = CreateTerrainSurface();

		database.BeginSync();
		database.TouchTerrainChunk(
			entity,
			0,
			terrainMesh,
			terrainMaterial,
			terrainMesh.BoundingSphere,
			instanceData,
			surface,
			new TerrainRayTracingChunkData(0, 16, 1, instanceData.ChunkOriginSize, instanceData.HeightmapUvScaleOffset),
			Matrix4x4.Identity);
		database.EndSync();
		var updates = new List<GpuDrawUpdate>();
		database.CopyUpdates(updates);
		resources.RecordUpdate(context, renderer, updates);
		database.ConsumeUpdates(updates);

		database.BeginSync();
		database.TouchTerrainChunk(
			entity,
			0,
			terrainMesh,
			terrainMaterial,
			terrainMesh.BoundingSphere,
			instanceData,
			surface,
			new TerrainRayTracingChunkData(0, 16, 2, instanceData.ChunkOriginSize, instanceData.HeightmapUvScaleOffset),
			Matrix4x4.Identity);
		database.EndSync();
		database.CopyUpdates(updates);
		commandList.ResetCounts();

		resources.RecordUpdate(context, renderer, updates);

		Assert.That(resources.LastStats.PendingBottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(0));
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(0));
	}

	[Test]
	public void RecordUpdate_TransformAndMeshSwapMarkTlasDirtyButCameraFreeFrameDoesNot()
	{
		var database = new GpuDrawDatabase();
		var meshA = CreateTestMesh();
		var meshB = CreateOffsetMesh();
		var material = new Material("opaque");
		var entity = new Entity(1, 1);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources(new ShaderCompiler());
		var renderer = new TestRenderer(new TestDevice());

		database.BeginSync();
		database.TouchMesh(entity, meshA, material, Matrix4x4.Identity);
		database.EndSync();
		var updates = new List<GpuDrawUpdate>();
		database.CopyUpdates(updates);
		resources.RecordUpdate(context, renderer, updates);
		database.ConsumeUpdates(updates);

		database.BeginSync();
		database.TouchMesh(entity, meshA, material, Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f));
		database.EndSync();
		database.CopyUpdates(updates);
		commandList.ResetCounts();
		resources.RecordUpdate(context, renderer, updates);
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.Transform));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(1));
		database.ConsumeUpdates(updates);

		database.BeginSync();
		database.TouchMesh(entity, meshB, material, Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f));
		database.EndSync();
		database.CopyUpdates(updates);
		commandList.ResetCounts();
		resources.RecordUpdate(context, renderer, updates);
		Assert.That(resources.LastStats.TopLevelRebuildReason.HasFlag(RayTracingSceneRebuildReason.Mesh), Is.True);
		Assert.That(resources.LastStats.PendingBottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(1));
		database.ConsumeUpdates(updates);

		commandList.ResetCounts();
		resources.RecordUpdate(context, renderer, Array.Empty<GpuDrawUpdate>());
		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(0));
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.None));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(0));
	}

	private static RenderGraphContext CreateContext(GpuDrawDatabase database, TestCommandList commandList)
	{
		var context = new RenderGraphContext(new RenderGraphResourceRegistry(), "RayTracingSceneResourcesTest")
		{
			CommandList = commandList,
			GpuDrawDatabase = database
		};
		return context;
	}

	private static Mesh CreateTestMesh()
	{
		return new Mesh(
			[
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
			],
			[0u, 1u, 2u]);
	}

	private static Mesh CreateOffsetMesh()
	{
		return new Mesh(
			[
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(2.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(0.0f, 2.0f, 0.0f, 1.0f)
			],
			[0u, 1u, 2u]);
	}

	private static TerrainDrawSurface CreateTerrainSurface()
	{
		var heightmap = new Texture(
			"terrain-height",
			2,
			2,
			false,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(2, 2, new byte[16])]);
		heightmap.MarkGpuResourcesCreated(new TestTextureResources());
		return new TerrainDrawSurface(
			heightmap,
			layerIndexMap: null,
			layerWeightMap: null,
			heightScale: 16.0f,
			layerCount: 1,
			heightBlendSharpness: 4.0f,
			layers:
			[
				new TerrainResolvedLayer(null, null, null, null, 8.0f)
			]);
	}

	private static TerrainChunkInstanceData CreateTerrainInstanceData()
	{
		return new TerrainChunkInstanceData(
			new Vector4(0.0f, 0.0f, 8.0f, 8.0f),
			new Vector4(0.25f, 0.25f, 0.0f, 0.0f));
	}

	private static Vector3 SphericalFibonacci(int sampleIndex, int sampleCount)
	{
		const float goldenAngle = 2.39996322973f;
		var sample = sampleIndex + 0.5f;
		var cosTheta = 1.0f - 2.0f * sample / sampleCount;
		var sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
		var phi = sampleIndex * goldenAngle;
		return new Vector3(MathF.Cos(phi) * sinTheta, cosTheta, MathF.Sin(phi) * sinTheta);
	}

	private static DdgiRelocationHit[] CreateRelocationMisses()
	{
		var hits = new DdgiRelocationHit[DdgiUtilities.RelocationRayCount];
		for (var index = 0; index < hits.Length; index++)
		{
			hits[index] = new DdgiRelocationHit(
				DdgiUtilities.GetRelocationRayDirection(index),
				10.0f,
				Valid: false);
		}

		return hits;
	}

	private static void AssertVector3(Vector3 actual, Vector3 expected, float tolerance = 1e-6f)
	{
		Assert.That(actual.X, Is.EqualTo(expected.X).Within(tolerance));
		Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(tolerance));
		Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(tolerance));
	}

	private sealed class TestRenderer : IRenderer
	{
		private readonly IGfxDevice _device;
		private readonly TestBuffer _vertexBuffer = new(BufferUsage.Vertex);
		private readonly TestBuffer _indexBuffer = new(BufferUsage.Index);

		public TestRenderer(IGfxDevice device)
		{
			_device = device;
		}

		public void Run(Action startup, Action<float> update, Action<float> render) => throw new NotSupportedException();
		public IMaterialResources CreateMaterialResources(Material material) => throw new NotSupportedException();
		public ITextureResources CreateTextureResources(Texture texture) => throw new NotSupportedException();
		public IGfxDevice GetGfxDevice() => _device;
		public Int2 GetFrameBufferSize() => throw new NotSupportedException();
		public Int2 GetWindowSize() => throw new NotSupportedException();
		public void BeginFrame() => throw new NotSupportedException();
		public void Render(RenderGraphResourceRegistry resourceRegistry, RenderGraphResourceHandle finalColor) => throw new NotSupportedException();
		public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height) => throw new NotSupportedException();
		public void ReleaseMeshResources(Mesh mesh) { }
		public IGfxBuffer GetPackedMeshVertexBuffer() => _vertexBuffer;
		public IGfxBuffer GetPackedMeshIndexBuffer() => _indexBuffer;
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

		public void EnsureMeshResources(Mesh mesh)
		{
			mesh.VertexBuffer ??= _vertexBuffer;
			mesh.IndexBuffer ??= _indexBuffer;
			mesh.StrideInBytes = 48;
			mesh.IndexCount = (uint)mesh.Indices.Length;
		}
	}

	private sealed class TestDevice : IGfxDevice, IGpuSubmissionTimeline
	{
		private readonly GpuRetirementQueue _retirementQueue = new();
		private readonly object _retirementTokenOwner = new();
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;
		public bool SupportsRayTracing => true;
		public ulong LastSubmittedId { get; set; }
		public ulong CompletedId { get; set; }
		public GpuRetirementStats RetirementStats => _retirementQueue.Stats;
		public GpuSubmissionToken LastPrimarySubmission { get; private set; }
		public void PumpCompleted() => _retirementQueue.ReleaseCompleted(CompletedId);
		public void Retire(Action release, string? name = null) => _retirementQueue.Retire(release, name);
		public void RetireAfter(GpuSubmissionToken submission, Action release, string? name = null)
		{
			if (submission.BelongsTo(_retirementTokenOwner) == false)
			{
				throw new InvalidOperationException("Foreign submission token.");
			}

			_retirementQueue.RetireAfterSubmission(release, name, submission.Value);
		}
		public IGfxDescriptorTable GlobalTable { get; } = new TestDescriptorTable();
		public IGfxCommandList BeginGraphics() => throw new NotSupportedException();
		public IGfxCommandList BeginCompute() => throw new NotSupportedException();
		public void Submit(IGfxCommandList commandList, GpuSubmissionKind submissionKind = GpuSubmissionKind.Auxiliary) =>
			throw new NotSupportedException();
		public void WaitForIdle() => _retirementQueue.ReleaseAllAfterIdle();
		public IGfxTexture CreateTexture(in TextureDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor) => new TestBuffer(descriptor);
		public IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBottomLevelAccelerationStructure CreateBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor) => new TestBottomLevelAccelerationStructure(descriptor);
		public IGfxTopLevelAccelerationStructure CreateTopLevelAccelerationStructure(in TopLevelAccelerationStructureDescriptor descriptor) => new TestTopLevelAccelerationStructure(descriptor);
		public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders) => new TestPipeline(key);
		public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder() => throw new NotSupportedException();
	}

	private sealed class TestCommandList : IGfxCommandList
	{
		public int BottomLevelBuildCount { get; private set; }
		public int TopLevelBuildCount { get; private set; }
		public List<IGfxBottomLevelAccelerationStructure> BottomLevelBuilds { get; } = new();
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;
		public void ResetCounts()
		{
			BottomLevelBuildCount = 0;
			TopLevelBuildCount = 0;
			BottomLevelBuilds.Clear();
		}

		public void BuildBottomLevelAccelerationStructure(IGfxBottomLevelAccelerationStructure accelerationStructure)
		{
			BottomLevelBuildCount++;
			BottomLevelBuilds.Add(accelerationStructure);
		}
		public void BuildTopLevelAccelerationStructure(IGfxTopLevelAccelerationStructure accelerationStructure, ReadOnlySpan<RayTracingInstanceDescription> instances) => TopLevelBuildCount++;
		public void SynchronizeAccelerationStructureBuildForComputeRead(IGfxTopLevelAccelerationStructure accelerationStructure) { }
		public void BeginPass(in PassTargets targets, in Viewport viewport) => throw new NotSupportedException();
		public void EndPass() => throw new NotSupportedException();
		public void BindPipeline(IGfxPipeline pipeline) { }
		public void SetPrimitiveTopology(PrimitiveTopology topology) => throw new NotSupportedException();
		public void SetScissorRect(in RectInt rect) => throw new NotSupportedException();
		public void ClearColorAttachment(uint index, ColorRGBA color) => throw new NotSupportedException();
		public void ClearDepthStencil(float depth) => throw new NotSupportedException();
		public void BindGraphicsDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet) => throw new NotSupportedException();
		public void BindComputeDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet) => throw new NotSupportedException();
		public void SetBindlessTable(IGfxDescriptorTable table) => throw new NotSupportedException();
		public void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0) => throw new NotSupportedException();
		public void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data) => throw new NotSupportedException();
		public void SetComputeConstants(uint slot, ReadOnlySpan<byte> data) { }
		public void SetComputeBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0) { }
		public void SetComputeReadOnlyBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0) { }
		public void PushConstants<T>(in T data) where T : unmanaged => throw new NotSupportedException();
		public void SetVertexBuffer(in VertexBufferView vertexBuffer) => throw new NotSupportedException();
		public void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers) => throw new NotSupportedException();
		public void SetIndexBuffer(in IndexBufferView indexBuffer) => throw new NotSupportedException();
		public void Draw(in DrawArguments arguments) => throw new NotSupportedException();
		public void DrawIndexedIndirect(in IndexBufferView indexBuffer, IGfxBuffer indirectArgsBuffer, ulong indirectArgsOffset) => throw new NotSupportedException();
		public void ExecuteIndirectCommandBuffer(IGfxIndirectCommandBuffer commandBuffer, uint maxCommandCount) => throw new NotSupportedException();
		public void ExecuteIndirectCommandBufferRange(IGfxIndirectCommandBuffer commandBuffer, IGfxBuffer commandRangeBuffer, ulong commandRangeOffsetBytes) => throw new NotSupportedException();
		public void ExecuteCompactedIndirectCommandBuffer(IGfxIndirectCommandBuffer commandBuffer, IGfxBuffer countBuffer, ulong countOffsetBytes) => throw new NotSupportedException();
		public void SetComputeAccelerationStructure(uint slot, IGfxTopLevelAccelerationStructure accelerationStructure) => throw new NotSupportedException();
		public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
		public void CopyBuffer(IGfxBuffer source, ulong sourceOffset, IGfxBuffer destination, ulong destinationOffset, ulong sizeInBytes) => throw new NotSupportedException();
		public void Barrier(in ResourceBarrierDescription barrier) { }
	}

	private sealed class TestBottomLevelAccelerationStructure : IGfxBottomLevelAccelerationStructure, IDisposable
	{
		public TestBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor)
		{
			Descriptor = descriptor;
		}

		public string? Name => null;
		public BottomLevelAccelerationStructureDescriptor Descriptor { get; }
		public bool IsDisposed { get; private set; }
		public void Dispose() => IsDisposed = true;
	}

	private sealed class TestTopLevelAccelerationStructure : IGfxTopLevelAccelerationStructure
	{
		public TestTopLevelAccelerationStructure(in TopLevelAccelerationStructureDescriptor descriptor)
		{
			Descriptor = descriptor;
		}

		public string? Name => null;
		public TopLevelAccelerationStructureDescriptor Descriptor { get; }
	}

	private sealed class TestPipeline(PipelineKey key) : IGfxPipeline
	{
		public string? Name => null;
		public PipelineKey Key { get; } = key;
	}

	private sealed class TestBuffer : IGfxBuffer
	{
		public TestBuffer(BufferUsage usage)
		{
			Descriptor = new BufferDescriptor(256, usage);
		}

		public TestBuffer(in BufferDescriptor descriptor)
		{
			Descriptor = descriptor;
		}

		public string? Name => null;
		public BufferDescriptor Descriptor { get; }
	}

	private sealed class TestTextureResources : ITextureResources
	{
		public IGfxTexture Texture { get; } = new TestTexture();
		public DescriptorHandle ShaderResourceView { get; } = new(DescriptorKind.ShaderResourceView, 42);
	}

	private sealed class TestTexture : IGfxTexture
	{
		public string? Name => null;
		public TextureDescriptor Descriptor { get; } = new(2, 2, TextureFormat.Rgba8Unorm, TextureUsage.ShaderResource);
		public DescriptorHandle ShaderResourceView { get; } = new(DescriptorKind.ShaderResourceView, 42);
		public DescriptorHandle DepthShaderResourceView => DescriptorHandle.Invalid;
		public DescriptorHandle UnorderedAccessView => DescriptorHandle.Invalid;
	}

	private sealed class TestDescriptorTable : IGfxDescriptorTable
	{
		public DescriptorHandle AllocateShaderResourceView(IGfxResource resource) => throw new NotSupportedException();
		public DescriptorHandle AllocateDepthShaderResourceView(IGfxTexture texture) => throw new NotSupportedException();
		public DescriptorHandle AllocateUnorderedAccessView(IGfxResource resource) => throw new NotSupportedException();
		public DescriptorHandle AllocateConstantBufferView(IGfxBuffer buffer) => throw new NotSupportedException();
		public DescriptorHandle AllocateSampler(in SamplerDescriptor sampler) => throw new NotSupportedException();
		public BindlessFallbackHandles GetOrCreateFallbackHandles() => throw new NotSupportedException();
		public void Free(DescriptorHandle handle) => throw new NotSupportedException();
	}
}
