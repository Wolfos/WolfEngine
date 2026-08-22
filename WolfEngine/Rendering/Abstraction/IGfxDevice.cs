namespace WolfEngine.Rendering.Abstraction;

/// <summary>
/// API-agnostic device interface used by the render graph to allocate resources and create command lists.
/// </summary>
public interface IGfxDevice
{
	GraphicsBackendKind BackendKind { get; }

	/// <summary>
	/// Indicates whether the device can execute the engine's inline ray-query workloads.
	/// </summary>
	bool SupportsRayTracing { get; }

	/// <summary>
	/// Begins recording a graphics command list targeting the primary graphics queue.
	/// </summary>
	IGfxCommandList BeginGraphics();

	/// <summary>
	/// Begins recording a compute command list targeting the compute queue.
	/// </summary>
	IGfxCommandList BeginCompute();

	/// <summary>
	/// Submits a previously recorded command list to the appropriate GPU queue.
	/// Only <see cref="GpuSubmissionKind.PrimaryFrame"/> seals queued retirements; auxiliary uploads
	/// and precomputation submissions must not shorten resources needed by the primary frame.
	/// </summary>
	void Submit(
		IGfxCommandList commandList,
		GpuSubmissionKind submissionKind = GpuSubmissionKind.Auxiliary);

	/// <summary>
	/// Queues a GPU-visible resource release. The release is sealed to the next successful primary-frame
	/// submission and executes only after that submission has completed. Use this instead of disposing
	/// published resources or recycling their bindless descriptors directly.
	/// </summary>
	void Retire(Action release, string? name = null);

	/// <summary>
	/// Queues a release against a submission token previously issued by this device. This is intended
	/// for resources whose final use is already submitted, such as records reclaimed from the previous
	/// frame. Tokens from another device or invalid tokens are rejected.
	/// </summary>
	void RetireAfter(GpuSubmissionToken submission, Action release, string? name = null);

	/// <summary>
	/// The most recent successful primary-frame submission issued by this device.
	/// </summary>
	GpuSubmissionToken LastPrimarySubmission { get; }

	/// <summary>
	/// Current device-owned GPU retirement diagnostics.
	/// </summary>
	GpuRetirementStats RetirementStats { get; }

	/// <summary>
	/// Blocks until all previously submitted GPU work has completed.
	/// Unsealed retirements are also released because callers must not retain unsubmitted command lists
	/// across an idle boundary.
	/// </summary>
	void WaitForIdle();

	/// <summary>
	/// Allocates a new GPU texture from the supplied abstract descriptor.
	/// </summary>
	IGfxTexture CreateTexture(in TextureDescriptor descriptor);

	/// <summary>
	/// Allocates a new GPU buffer from the supplied descriptor.
	/// </summary>
	IGfxBuffer CreateBuffer(in BufferDescriptor descriptor);

	/// <summary>
	/// Allocates a new indirect command buffer from the supplied descriptor.
	/// </summary>
	IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor);

	IGfxBottomLevelAccelerationStructure CreateBottomLevelAccelerationStructure(
		in BottomLevelAccelerationStructureDescriptor descriptor);

	IGfxTopLevelAccelerationStructure CreateTopLevelAccelerationStructure(
		in TopLevelAccelerationStructureDescriptor descriptor);

	/// <summary>
	/// Retrieves an existing pipeline matching the key or creates one using the provided shader bytecodes.
	/// </summary>
	IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders);

	/// <summary>Waits for the caller to establish a safe frame boundary, then releases cached pipeline states.</summary>
	void ClearPipelineCache() { }

	/// <summary>
	/// Global bindless descriptor table shared across passes and command lists.
	/// </summary>
	IGfxDescriptorTable GlobalTable { get; }

	/// <summary>
	/// Creates a backend-specific descriptor set builder.
	/// </summary>
	IGfxDescriptorSetBuilder CreateDescriptorSetBuilder();
}

public enum GpuSubmissionKind
{
	Auxiliary,
	PrimaryFrame
}

public readonly struct GpuSubmissionToken : IEquatable<GpuSubmissionToken>
{
	private readonly object? _owner;

	internal GpuSubmissionToken(object owner, ulong value)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		if (value == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(value), "Submission IDs must be non-zero.");
		}

		Value = value;
	}

	public static GpuSubmissionToken Invalid => default;
	public bool IsValid => _owner is not null && Value != 0;
	public ulong Value { get; }

	internal bool BelongsTo(object owner) => ReferenceEquals(_owner, owner);

	public bool Equals(GpuSubmissionToken other) =>
		ReferenceEquals(_owner, other._owner) && Value == other.Value;

	public override bool Equals(object? obj) => obj is GpuSubmissionToken other && Equals(other);
	public override int GetHashCode() => HashCode.Combine(_owner, Value);
}

public static class GfxDeviceRetirementExtensions
{
	public static void Retire(this IGfxDevice device, IDisposable resource, string? name = null)
	{
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(resource);
		device.Retire(resource.Dispose, name ?? resource.GetType().Name);
	}

	public static void RetireAfter(
		this IGfxDevice device,
		GpuSubmissionToken submission,
		IDisposable resource,
		string? name = null)
	{
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(resource);
		device.RetireAfter(submission, resource.Dispose, name ?? resource.GetType().Name);
	}
}

/// <summary>
/// Optionally implemented by backends that support pooling of transient textures.
/// </summary>
public interface ITexturePoolDevice
{
	/// <summary>
	/// Attempts to return a texture to the pool instead of disposing it.
	/// Returns true if the texture was successfully pooled.
	/// </summary>
	bool ReturnTexture(IGfxTexture texture, ResourceState lastKnownState);

	/// <summary>
	/// Clears any pooled textures, releasing their resources.
	/// </summary>
	void ClearTexturePool();
}
