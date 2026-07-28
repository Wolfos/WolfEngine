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

		device.SubmitFrame(11UL);
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
		private readonly GpuRetirementQueue _retirementQueue = new();
		private readonly object _retirementTokenOwner = new();

		public TestDevice(params IGfxTexture[] textures)
		{
			_textures = new Queue<IGfxTexture>(textures);
		}

		public List<(IGfxTexture Texture, ResourceState State)> ReturnedTextures { get; } = new();
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.D3D12;
		public bool SupportsRayTracing => false;
		public ulong LastSubmittedId { get; set; }
		public ulong CompletedId { get; set; }
		public GpuRetirementStats RetirementStats => _retirementQueue.Stats;
		public GpuSubmissionToken LastPrimarySubmission { get; private set; }
		public IGfxDescriptorTable GlobalTable => throw new NotSupportedException();

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
		public void SubmitFrame(ulong submissionId)
		{
			var batch = _retirementQueue.PrepareSubmission(GpuSubmissionKind.PrimaryFrame);
			LastSubmittedId = submissionId;
			LastPrimarySubmission = new GpuSubmissionToken(_retirementTokenOwner, submissionId);
			_retirementQueue.SealSubmission(batch, submissionId);
		}
		public IGfxCommandList BeginGraphics() => throw new NotSupportedException();
		public IGfxCommandList BeginCompute() => throw new NotSupportedException();
		public void Submit(IGfxCommandList commandList, GpuSubmissionKind submissionKind = GpuSubmissionKind.Auxiliary) =>
			throw new NotSupportedException();
		public void WaitForIdle() => _retirementQueue.ReleaseAllAfterIdle();
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
