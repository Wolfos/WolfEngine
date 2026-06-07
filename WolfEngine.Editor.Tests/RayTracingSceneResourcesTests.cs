using System.Numerics;
using System.Text.Json;
using WolfEngine;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class RayTracingSceneResourcesTests
{
	[Test]
	public void RtaoShaderCompilesForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetComputeShaderWithReflection(
			"ao_rtao.compute.slang",
			"CSMain",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));
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
				         "ddgi_trace.compute.slang",
				         "ddgi_relocate.compute.slang",
				         "ddgi_integrate.compute.slang",
			         "ddgi_border_update.compute.slang"
		         })
		{
			var compiled = shaderCompiler.GetComputeShaderWithReflection(
				shader,
				"CSMain",
				GraphicsBackendKind.Metal);

			Assert.That(compiled.Bytecode.IsEmpty, Is.False, shader);
			Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8), shader);
			Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8), shader);
		}
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
			"deferred_lighting.compute.slang",
			"CSMain",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));
	}

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
		Assert.That(ddgi.Hysteresis, Is.EqualTo(0.95f));
		Assert.That(ddgi.ProbeRelocationEnabled, Is.True);
		Assert.That(ddgi.ProbeMinFrontfaceDistance, Is.EqualTo(0.2f));
		Assert.That(ddgi.ProbeBackfaceThreshold, Is.EqualTo(0.25f));
		Assert.That(ddgi.ProbeMaxRelocationDistanceFactor, Is.EqualTo(0.45f));
		Assert.That(ddgi.DebugProbeSpheres, Is.False);
		Assert.That(ddgi.DebugProbeSphereRadius, Is.EqualTo(0.15f));

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
					Hysteresis = 0.8f,
					ProbeRelocationEnabled = false,
					ProbeMinFrontfaceDistance = 0.3f,
					ProbeBackfaceThreshold = 0.4f,
					ProbeMaxRelocationDistanceFactor = 0.35f,
				DebugProbeSpheres = true,
				DebugProbeSphereRadius = 0.3f
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
		Assert.That(ddgi.Hysteresis, Is.EqualTo(0.8f));
		Assert.That(ddgi.ProbeRelocationEnabled, Is.False);
		Assert.That(ddgi.ProbeMinFrontfaceDistance, Is.EqualTo(0.3f));
		Assert.That(ddgi.ProbeBackfaceThreshold, Is.EqualTo(0.4f));
		Assert.That(ddgi.ProbeMaxRelocationDistanceFactor, Is.EqualTo(0.35f));
		Assert.That(ddgi.DebugProbeSpheres, Is.True);
		Assert.That(ddgi.DebugProbeSphereRadius, Is.EqualTo(0.3f));
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
		Assert.That(halfway, Is.GreaterThan(perpendicular));
		Assert.That(halfway, Is.LessThan(facing));
	}

	[Test]
	public void RenderPipeline_DdgiProbeDebugToggleInjectsAlphaBlendedSpherePrimitives()
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

		database.BeginSync();
		RenderPipeline.CollectDdgiProbeDebugPrimitives(config, database, meshFactory);
		database.EndSync();

		var entries = new List<GpuDrawEntry>();
		database.CollectDrawEntries(entries);

		Assert.That(entries, Has.Count.EqualTo(4));
		Assert.That(entries.Select(entry => entry.DrawKind), Is.All.EqualTo(GpuDrawKind.DebugPrimitive));
		Assert.That(entries.Select(entry => entry.Material.AlphaMode), Is.All.EqualTo(AlphaMode.AlphaBlend));
		Assert.That(entries.Select(entry => entry.Material.Color), Is.All.EqualTo(ColorRGBA.White));
		Assert.That(entries.Select(entry => entry.World.M11), Is.All.EqualTo(0.5f).Within(0.0001f));
	}

	[Test]
	public void RecordUpdate_BootstrapBuildsOpaqueMeshSceneAndReportsSkippedDraws()
	{
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
		var resources = new RayTracingSceneResources();

		resources.RecordUpdate(context, new TestRenderer(new TestDevice()), updates);

		Assert.That(resources.LastStats.BottomLevelAccelerationStructureCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelInstanceCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.PendingBottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.Bootstrap));
		Assert.That(resources.LastStats.SkippedTerrainCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.SkippedTransparentOrAlphaCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.SidecarHitShadingAvailable, Is.True);
		Assert.That(resources.InstanceIndexToInstanceHandleBuffer, Is.Not.Null);
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(1));
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
		var resources = new RayTracingSceneResources();
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
	public void RecordUpdate_TransformAndMeshSwapMarkTlasDirtyButCameraFreeFrameDoesNot()
	{
		var database = new GpuDrawDatabase();
		var meshA = CreateTestMesh();
		var meshB = CreateOffsetMesh();
		var material = new Material("opaque");
		var entity = new Entity(1, 1);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources();
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
		return new TerrainDrawSurface(
			heightmap: null,
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
			mesh.StrideInBytes = 16;
			mesh.IndexCount = (uint)mesh.Indices.Length;
		}
	}

	private sealed class TestDevice : IGfxDevice
	{
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;
		public IGfxDescriptorTable GlobalTable { get; } = new TestDescriptorTable();
		public IGfxCommandList BeginGraphics() => throw new NotSupportedException();
		public IGfxCommandList BeginCompute() => throw new NotSupportedException();
		public void Submit(IGfxCommandList commandList) => throw new NotSupportedException();
		public void WaitForIdle() => throw new NotSupportedException();
		public IGfxTexture CreateTexture(in TextureDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor) => new TestBuffer(descriptor.Usage);
		public IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBottomLevelAccelerationStructure CreateBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor) => new TestBottomLevelAccelerationStructure(descriptor);
		public IGfxTopLevelAccelerationStructure CreateTopLevelAccelerationStructure(in TopLevelAccelerationStructureDescriptor descriptor) => new TestTopLevelAccelerationStructure(descriptor);
		public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders) => throw new NotSupportedException();
		public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder() => throw new NotSupportedException();
	}

	private sealed class TestCommandList : IGfxCommandList
	{
		public int BottomLevelBuildCount { get; private set; }
		public int TopLevelBuildCount { get; private set; }
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;
		public void ResetCounts()
		{
			BottomLevelBuildCount = 0;
			TopLevelBuildCount = 0;
		}

		public void BuildBottomLevelAccelerationStructure(IGfxBottomLevelAccelerationStructure accelerationStructure) => BottomLevelBuildCount++;
		public void BuildTopLevelAccelerationStructure(IGfxTopLevelAccelerationStructure accelerationStructure, ReadOnlySpan<RayTracingInstanceDescription> instances) => TopLevelBuildCount++;
		public void SynchronizeAccelerationStructureBuildForComputeRead(IGfxTopLevelAccelerationStructure accelerationStructure) { }
		public void BeginPass(in PassTargets targets, in Viewport viewport) => throw new NotSupportedException();
		public void EndPass() => throw new NotSupportedException();
		public void BindPipeline(IGfxPipeline pipeline) => throw new NotSupportedException();
		public void SetPrimitiveTopology(PrimitiveTopology topology) => throw new NotSupportedException();
		public void SetScissorRect(in RectInt rect) => throw new NotSupportedException();
		public void ClearColorAttachment(uint index, ColorRGBA color) => throw new NotSupportedException();
		public void ClearDepthStencil(float depth) => throw new NotSupportedException();
		public void BindGraphicsDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet) => throw new NotSupportedException();
		public void BindComputeDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet) => throw new NotSupportedException();
		public void SetBindlessTable(IGfxDescriptorTable table) => throw new NotSupportedException();
		public void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0) => throw new NotSupportedException();
		public void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data) => throw new NotSupportedException();
		public void SetComputeConstants(uint slot, ReadOnlySpan<byte> data) => throw new NotSupportedException();
		public void SetComputeBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0) => throw new NotSupportedException();
		public void PushConstants<T>(in T data) where T : unmanaged => throw new NotSupportedException();
		public void SetVertexBuffer(in VertexBufferView vertexBuffer) => throw new NotSupportedException();
		public void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers) => throw new NotSupportedException();
		public void SetIndexBuffer(in IndexBufferView indexBuffer) => throw new NotSupportedException();
		public void Draw(in DrawArguments arguments) => throw new NotSupportedException();
		public void DrawIndexedIndirect(in IndexBufferView indexBuffer, IGfxBuffer indirectArgsBuffer, ulong indirectArgsOffset) => throw new NotSupportedException();
		public void ExecuteIndirectCommandBuffer(IGfxIndirectCommandBuffer commandBuffer, uint maxCommandCount) => throw new NotSupportedException();
		public void ExecuteIndirectCommandBufferIndexed(IGfxIndirectCommandBuffer commandBuffer, IGfxBuffer commandIndicesBuffer, ulong indicesOffsetBytes, IGfxBuffer commandCountBuffer, ulong commandCountOffsetBytes) => throw new NotSupportedException();
		public void SetComputeAccelerationStructure(uint slot, IGfxTopLevelAccelerationStructure accelerationStructure) => throw new NotSupportedException();
		public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => throw new NotSupportedException();
		public void CopyBuffer(IGfxBuffer source, ulong sourceOffset, IGfxBuffer destination, ulong destinationOffset, ulong sizeInBytes) => throw new NotSupportedException();
		public void Barrier(in ResourceBarrierDescription barrier) => throw new NotSupportedException();
	}

	private sealed class TestBottomLevelAccelerationStructure : IGfxBottomLevelAccelerationStructure
	{
		public TestBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor)
		{
			Descriptor = descriptor;
		}

		public string? Name => null;
		public BottomLevelAccelerationStructureDescriptor Descriptor { get; }
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

	private sealed class TestBuffer : IGfxBuffer
	{
		public TestBuffer(BufferUsage usage)
		{
			Descriptor = new BufferDescriptor(256, usage);
		}

		public string? Name => null;
		public BufferDescriptor Descriptor { get; }
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
