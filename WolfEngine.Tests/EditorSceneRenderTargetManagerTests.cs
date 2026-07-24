using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

public sealed class EditorSceneRenderTargetManagerTests
{
	[Test]
	public void ResizeWaitsForTheUiFrameThatStillReferencesTheOldTexture()
	{
		var oldTexture = new TestTexture();
		var resizedTexture = new TestTexture();
		var device = new TestDevice(oldTexture, resizedTexture);

		using var manager = new EditorSceneRenderTargetManager();
		manager.EnsureTarget(device, new Int2(640, 360));
		manager.SetCurrentState(ResourceState.ShaderResource);

		device.LastSubmittedId = 10UL;
		device.CompletedId = 10UL;
		manager.EnsureTarget(device, new Int2(1280, 720));

		Assert.That(device.ReturnedTextures, Is.Empty);

		device.LastSubmittedId = 11UL;
		device.CompletedId = 10UL;
		manager.Advance(device);

		Assert.That(device.ReturnedTextures, Is.Empty);

		device.CompletedId = 11UL;
		manager.Advance(device);

		Assert.That(device.ReturnedTextures, Has.Count.EqualTo(1));
		Assert.Multiple(() =>
		{
			Assert.That(device.ReturnedTextures[0].Texture, Is.SameAs(oldTexture));
			Assert.That(device.ReturnedTextures[0].State, Is.EqualTo(ResourceState.ShaderResource));
		});
	}

	private sealed class TestDevice : IGfxDevice, IGpuSubmissionTimeline, ITexturePoolDevice
	{
		private readonly Queue<IGfxTexture> _textures;

		public TestDevice(params IGfxTexture[] textures)
		{
			_textures = new Queue<IGfxTexture>(textures);
		}

		public List<(IGfxTexture Texture, ResourceState State)> ReturnedTextures { get; } = new();
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.D3D12;
		public bool SupportsRayTracing => false;
		public ulong LastSubmittedId { get; set; }
		public ulong CompletedId { get; set; }
		public IGfxDescriptorTable GlobalTable => throw new NotSupportedException();

		public void PumpCompleted() { }
		public IGfxCommandList BeginGraphics() => throw new NotSupportedException();
		public IGfxCommandList BeginCompute() => throw new NotSupportedException();
		public void Submit(IGfxCommandList commandList) => throw new NotSupportedException();
		public void WaitForIdle() => throw new NotSupportedException();
		public IGfxTexture CreateTexture(in TextureDescriptor descriptor) => _textures.Dequeue();
		public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor) => throw new NotSupportedException();
		public IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBottomLevelAccelerationStructure CreateBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor) => throw new NotSupportedException();
		public IGfxTopLevelAccelerationStructure CreateTopLevelAccelerationStructure(in TopLevelAccelerationStructureDescriptor descriptor) => throw new NotSupportedException();
		public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders) => throw new NotSupportedException();
		public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder() => throw new NotSupportedException();

		public bool ReturnTexture(IGfxTexture texture, ResourceState lastKnownState)
		{
			ReturnedTextures.Add((texture, lastKnownState));
			return true;
		}

		public void ClearTexturePool() { }
	}

	private sealed class TestTexture : IGfxTexture
	{
		public string? Name => null;
		public TextureDescriptor Descriptor => default;
		public DescriptorHandle ShaderResourceView => DescriptorHandle.Invalid;
		public DescriptorHandle DepthShaderResourceView => DescriptorHandle.Invalid;
		public DescriptorHandle UnorderedAccessView => DescriptorHandle.Invalid;
	}
}
