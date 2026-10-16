using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RenderGraphTransientPoolTests
{
	private static readonly TextureDescriptor ColorTarget = new(
		64,
		32,
		TextureFormat.Rgba16Float,
		TextureUsage.ShaderResource | TextureUsage.RenderTarget);

	[Test]
	public void NextFrameReusesTransientTexturesWhileThePreviousFrameIsStillInFlight()
	{
		var device = new TestDevice();
		var registry = new RenderGraphResourceRegistry();
		registry.SetDevice(device);

		var first = RunFrame(registry, device, 1, ColorTarget);
		// Submission 1 has not completed on the GPU.
		var second = RunFrame(registry, device, 2, ColorTarget);

		Assert.That(second, Is.SameAs(first));
		Assert.That(device.CreatedTextures, Has.Count.EqualTo(1));
	}

	[Test]
	public void InvalidatedTexturesAreDestroyedOnlyAfterTheFrameThatUsedThemCompletes()
	{
		var device = new TestDevice();
		var registry = new RenderGraphResourceRegistry();
		registry.SetDevice(device);
		var texture = (TestTexture)RunFrame(registry, device, 1, ColorTarget);

		registry.BeginFrame();
		registry.InvalidateTransientTexturePool();
		device.PumpCompleted();

		Assert.That(texture.IsDisposed, Is.False, "submission 1 may still be reading the texture");

		device.CompletedId = 1;
		device.PumpCompleted();

		Assert.That(texture.IsDisposed, Is.True);
	}

	[Test]
	public void TexturesNothingReusesAreReleasedAfterTheIdleLimit()
	{
		var device = new TestDevice();
		var registry = new RenderGraphResourceRegistry();
		registry.SetDevice(device);
		var texture = (TestTexture)RunFrame(registry, device, 1, ColorTarget);

		for (ulong submission = 2; submission < 200; submission++)
		{
			RunFrame(registry, device, submission, descriptor: null);
			device.CompletedId = submission;
			device.PumpCompleted();
		}

		Assert.That(texture.IsDisposed, Is.True);
		Assert.That(registry.TransientMemory.LiveTextures, Is.Zero);
	}

	[Test]
	public void TextureAcquiredBeforeAnInvalidationIsNotReused()
	{
		var device = new TestDevice();
		var registry = new RenderGraphResourceRegistry();
		registry.SetDevice(device);

		registry.BeginFrame();
		var stale = registry.GetTexture(registry.CreateTransientTexture(ColorTarget));
		registry.InvalidateTransientTexturePool();
		device.SubmitFrame(1);
		registry.EndFrame();

		var next = RunFrame(registry, device, 2, ColorTarget);

		Assert.That(next, Is.Not.SameAs(stale));
	}

	private static IGfxTexture? RunFrame(
		RenderGraphResourceRegistry registry,
		TestDevice device,
		ulong submission,
		TextureDescriptor? descriptor)
	{
		registry.BeginFrame();
		IGfxTexture? texture = null;
		if (descriptor is { } textureDescriptor)
		{
			texture = registry.GetTexture(registry.CreateTransientTexture(textureDescriptor));
		}

		device.SubmitFrame(submission);
		registry.EndFrame();

		return texture;
	}

	private sealed class TestDevice : IGfxDevice, IGpuSubmissionTimeline
	{
		private readonly GpuRetirementQueue _retirementQueue = new();
		private readonly object _retirementTokenOwner = new();

		public List<TestTexture> CreatedTextures { get; } = new();
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.D3D12;
		public bool SupportsRayTracing => false;
		public ulong LastSubmittedId { get; set; }
		public ulong CompletedId { get; set; }
		public GpuRetirementStats RetirementStats => _retirementQueue.Stats;
		public GpuSubmissionToken LastPrimarySubmission { get; private set; }
		public IGfxDescriptorTable GlobalTable => throw new NotSupportedException();

		public void PumpCompleted()
		{
			_retirementQueue.ReleaseCompleted(CompletedId);
		}

		public void Retire(Action release, string? name = null)
		{
			_retirementQueue.Retire(release, name);
		}

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

		public IGfxTexture CreateTexture(in TextureDescriptor descriptor)
		{
			var texture = new TestTexture(descriptor);
			CreatedTextures.Add(texture);

			return texture;
		}

		public IGfxCommandList BeginGraphics() => throw new NotSupportedException();
		public IGfxCommandList BeginCompute() => throw new NotSupportedException();
		public void Submit(IGfxCommandList commandList, GpuSubmissionKind submissionKind = GpuSubmissionKind.Auxiliary) =>
			throw new NotSupportedException();
		public void WaitForIdle() => _retirementQueue.ReleaseAllAfterIdle();
		public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor) => throw new NotSupportedException();
		public IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBottomLevelAccelerationStructure CreateBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor) => throw new NotSupportedException();
		public IGfxTopLevelAccelerationStructure CreateTopLevelAccelerationStructure(in TopLevelAccelerationStructureDescriptor descriptor) => throw new NotSupportedException();
		public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders) => throw new NotSupportedException();
		public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder() => throw new NotSupportedException();
	}

	private sealed class TestTexture : IGfxTexture, IDisposable
	{
		public TestTexture(TextureDescriptor descriptor)
		{
			Descriptor = descriptor;
		}

		public bool IsDisposed { get; private set; }
		public string? Name => null;
		public TextureDescriptor Descriptor { get; }
		public DescriptorHandle ShaderResourceView => DescriptorHandle.Invalid;
		public DescriptorHandle DepthShaderResourceView => DescriptorHandle.Invalid;
		public DescriptorHandle UnorderedAccessView => DescriptorHandle.Invalid;

		public void Dispose()
		{
			IsDisposed = true;
		}
	}
}
