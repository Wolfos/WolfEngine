using System.Numerics;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

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
