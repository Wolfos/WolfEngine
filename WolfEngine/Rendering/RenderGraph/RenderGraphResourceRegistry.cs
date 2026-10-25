using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Central book-keeping for logical render graph resources.
/// Responsible for allocating transient handles and resolving them to real GPU allocations later.
/// </summary>
public sealed class RenderGraphResourceRegistry
{
	private readonly struct TexturePoolKey : IEquatable<TexturePoolKey>
	{
		public TexturePoolKey(in TextureDescriptor descriptor)
		{
			Width = descriptor.Width;
			Height = descriptor.Height;
			Depth = descriptor.Depth;
			Dimension = descriptor.Dimension;
			Format = descriptor.Format;
			Usage = descriptor.Usage;
			ClearColor = descriptor.ClearColor;
			DepthClear = descriptor.DepthClear;
			MipLevels = descriptor.MipLevels;
			IsSrgb = descriptor.IsSrgb;
		}

		public int Width { get; }
		public int Height { get; }
		public int Depth { get; }
		public TextureDimension Dimension { get; }
		public TextureFormat Format { get; }
		public TextureUsage Usage { get; }
		public ColorRGBA ClearColor { get; }
		public float DepthClear { get; }
		public int MipLevels { get; }
		public bool IsSrgb { get; }

		public bool Equals(TexturePoolKey other)
		{
			return Width == other.Width &&
			       Height == other.Height &&
			       Depth == other.Depth &&
			       Dimension == other.Dimension &&
			       Format == other.Format &&
			       Usage == other.Usage &&
			       MipLevels == other.MipLevels &&
			       IsSrgb == other.IsSrgb &&
			       ClearColor.Equals(other.ClearColor) &&
			       DepthClear.Equals(other.DepthClear);
		}

		public override bool Equals(object? obj) => obj is TexturePoolKey other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(Width, Height, Depth | ((int)Dimension << 16), Format, Usage, MipLevels, IsSrgb, HashCode.Combine(ClearColor, DepthClear));
	}

	private readonly struct TransientPoolEntry
	{
		public TransientPoolEntry(IGfxTexture texture, ResourceState lastKnownState, GpuSubmissionToken lastUse, long returnedFrame)
		{
			Texture = texture;
			LastKnownState = lastKnownState;
			LastUse = lastUse;
			ReturnedFrame = returnedFrame;
		}

		public IGfxTexture Texture { get; }
		public ResourceState LastKnownState { get; }

		/// <summary>The submission of the last frame that used the texture. It is only destroyed once that completes.</summary>
		public GpuSubmissionToken LastUse { get; }

		public long ReturnedFrame { get; }
	}

	private readonly struct ActiveTransientSlot
	{
		public ActiveTransientSlot(int slotId, TexturePoolKey poolKey, IGfxTexture texture, ResourceState initialState, int poolEpoch)
		{
			SlotId = slotId;
			PoolKey = poolKey;
			Texture = texture;
			InitialState = initialState;
			PoolEpoch = poolEpoch;
		}

		public int SlotId { get; }
		public TexturePoolKey PoolKey { get; }
		public IGfxTexture Texture { get; }
		public ResourceState InitialState { get; }

		/// <summary>The pool epoch the texture was acquired in. Textures from before an invalidation are not reused.</summary>
		public int PoolEpoch { get; }
	}

	private sealed class TextureRecord
	{
		public TextureDescriptor Descriptor;
		public bool OwnsTexture;
		public IGfxTexture? Texture;
		public ResourceState InitialState;
		public ResourceState CurrentState;
		public bool IsTransient;
		public int TransientSlotId;
		public int StateTrackingKey;

		public void InitializeTransient(TextureDescriptor descriptor, ResourceState initialState, int handleId)
		{
			Descriptor = descriptor;
			OwnsTexture = true;
			Texture = null;
			InitialState = initialState;
			CurrentState = initialState;
			IsTransient = true;
			TransientSlotId = handleId;
			StateTrackingKey = -handleId;
		}

		public void InitializeImported(TextureDescriptor descriptor, bool ownsTexture, IGfxTexture texture, ResourceState initialState, int handleId)
		{
			Descriptor = descriptor;
			OwnsTexture = ownsTexture;
			Texture = texture;
			InitialState = initialState;
			CurrentState = initialState;
			IsTransient = false;
			TransientSlotId = 0;
			StateTrackingKey = handleId;
		}

		public void Reset()
		{
			Descriptor = default;
			OwnsTexture = false;
			Texture = null;
			InitialState = ResourceState.Common;
			CurrentState = ResourceState.Common;
			IsTransient = false;
			TransientSlotId = 0;
			StateTrackingKey = 0;
		}
	}

	private sealed class BufferRecord
	{
		public BufferDescriptor Descriptor;
		public bool OwnsBuffer;
		public IGfxBuffer? Buffer;
		public ResourceState InitialState;
		public ResourceState CurrentState;

		public void Initialize(BufferDescriptor descriptor, bool ownsBuffer, IGfxBuffer? buffer, ResourceState state)
		{
			Descriptor = descriptor;
			OwnsBuffer = ownsBuffer;
			Buffer = buffer;
			InitialState = state;
			CurrentState = state;
		}

		public void Reset()
		{
			Descriptor = default;
			OwnsBuffer = false;
			Buffer = null;
			InitialState = ResourceState.Common;
			CurrentState = ResourceState.Common;
		}
	}

	// Pooled transient textures nothing has reused for this many frames are released, so textures for a resolution or
	// feature no longer in use do not stay alive.
	private const int TransientPoolIdleFrameLimit = 120;

	private int _nextHandleId = 1;
	private int _transientPoolEpoch = 1;
	private long _frameIndex;
	private int _liveTransientTextureCount;
	private long _liveTransientTextureBytes;
	private TransientTextureMemory _lastFrameTransientMemory = new(0, 0, 0, 0, 0, 0, 0, 0);
	private readonly Dictionary<int, TextureRecord> _textures = new();
	private readonly Dictionary<int, BufferRecord> _buffers = new();
	private readonly Stack<TextureRecord> _textureRecordPool = new();
	private readonly Stack<BufferRecord> _bufferRecordPool = new();
	private readonly Dictionary<int, ActiveTransientSlot> _activeTransientSlots = new();
	private readonly Dictionary<int, ResourceState> _transientSlotStates = new();
	private readonly Dictionary<TexturePoolKey, Stack<TransientPoolEntry>> _availableTransientTextures = new();
	private readonly List<TransientPoolEntry> _retainedPoolEntries = new();
	private IGfxDevice? _device;
	private ITexturePoolDevice? _texturePoolDevice;
	private GpuSubmissionToken _previousFrameSubmission;
	private GpuSubmissionToken _frameStartPrimarySubmission;

	public void SetDevice(IGfxDevice device)
	{
		if (device is null)
		{
			throw new ArgumentNullException(nameof(device));
		}

		if (ReferenceEquals(_device, device))
		{
			_texturePoolDevice = device as ITexturePoolDevice;
			return;
		}

		if (_device is not null)
		{
			ReclaimFrameRecordsToDevice(_device, _texturePoolDevice, _previousFrameSubmission);
			_device.WaitForIdle();
			InvalidateTransientTexturePool();
		}

		_device = device;
		_texturePoolDevice = device as ITexturePoolDevice;
		_previousFrameSubmission = GpuSubmissionToken.Invalid;
		_frameStartPrimarySubmission = device.LastPrimarySubmission;
	}

	public void BeginFrame()
	{
		_frameIndex++;
		ReclaimFrameRecordsToDevice(_device, _texturePoolDevice, _previousFrameSubmission);
		ReleaseIdleTransientTextures();
		_previousFrameSubmission = GpuSubmissionToken.Invalid;
		_frameStartPrimarySubmission = _device?.LastPrimarySubmission ?? GpuSubmissionToken.Invalid;
		_nextHandleId = 1;
	}

	public void EndFrame()
	{
		PublishTransientMemory();

		// Resources remain tracked until the next BeginFrame call, where they are reclaimed.
		var submitted = _device?.LastPrimarySubmission ?? GpuSubmissionToken.Invalid;
		_previousFrameSubmission = submitted.IsValid && submitted.Equals(_frameStartPrimarySubmission) == false
			? submitted
			: GpuSubmissionToken.Invalid;
	}

	public void InvalidateTransientTexturePool()
	{
		_transientPoolEpoch++;
		DisposeAvailableTransientTextures();
	}

	/// <summary>The descriptor a texture handle was created or imported with; available before allocation.</summary>
	internal TextureDescriptor GetTextureDescriptor(RenderGraphResourceHandle handle) =>
		_textures.TryGetValue(handle.Id, out var record)
			? record.Descriptor
			: throw new KeyNotFoundException($"Texture handle {handle.Id} is not registered this frame.");

	public RenderGraphResourceHandle CreateTransientTexture(in TextureDescriptor descriptor)
	{
		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		var record = _textureRecordPool.Count > 0 ? _textureRecordPool.Pop() : new TextureRecord();
		record.InitializeTransient(descriptor, DetermineInitialState(descriptor.Usage), handle.Id);
		_textures[handle.Id] = record;
		return handle;
	}

	public RenderGraphResourceHandle ImportTexture(
		IGfxTexture texture,
		bool takeOwnership = false,
		ResourceState initialState = ResourceState.Common)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		var record = _textureRecordPool.Count > 0 ? _textureRecordPool.Pop() : new TextureRecord();
		record.InitializeImported(texture.Descriptor, takeOwnership, texture, initialState, handle.Id);
		_textures[handle.Id] = record;
		return handle;
	}

	public RenderGraphResourceHandle ImportBuffer(
		IGfxBuffer buffer,
		bool takeOwnership = false,
		ResourceState initialState = ResourceState.Common)
	{
		if (buffer is null)
		{
			throw new ArgumentNullException(nameof(buffer));
		}

		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		var record = _bufferRecordPool.Count > 0 ? _bufferRecordPool.Pop() : new BufferRecord();
		record.Initialize(buffer.Descriptor, takeOwnership, buffer, initialState);
		_buffers[handle.Id] = record;
		return handle;
	}

	internal void AssignTransientTextureSlots(IReadOnlyDictionary<int, int> handleToSlotAssignments)
	{
		// The compiler numbers alias slots from 1, while unused transients have no assignment. Their raw
		// handle IDs can equal a compiler slot from another view (for example a hidden or skipped pass),
		// even when the descriptors differ. Reserve a separate range for those unassigned handles.
		var maxAssignedSlotId = handleToSlotAssignments.Values.DefaultIfEmpty(0).Max();
		var slotCompatibility = new Dictionary<int, TexturePoolKey>();
		foreach (var (handleId, record) in _textures)
		{
			if (record.IsTransient == false)
			{
				record.StateTrackingKey = handleId;
				continue;
			}

			var slotId = handleToSlotAssignments.TryGetValue(handleId, out var assignedSlot) && assignedSlot > 0
				? assignedSlot
				: checked(maxAssignedSlotId + handleId);
			record.TransientSlotId = slotId;
			record.StateTrackingKey = -slotId;

			var key = new TexturePoolKey(record.Descriptor);
			if (slotCompatibility.TryGetValue(slotId, out var existingKey) && existingKey.Equals(key) == false)
			{
				throw new InvalidOperationException($"Transient alias slot {slotId} assigned to incompatible descriptors.");
			}

			slotCompatibility[slotId] = key;
		}
	}

	internal bool IsTransientTexture(RenderGraphResourceHandle handle)
	{
		return _textures.TryGetValue(handle.Id, out var record) && record.IsTransient;
	}

	internal bool TryGetTransientTextureDescriptor(RenderGraphResourceHandle handle, out TextureDescriptor descriptor)
	{
		if (_textures.TryGetValue(handle.Id, out var record) && record.IsTransient)
		{
			descriptor = record.Descriptor;
			return true;
		}

		descriptor = default;
		return false;
	}

	internal int GetStateTrackingKey(RenderGraphResourceHandle handle)
	{
		if (_textures.TryGetValue(handle.Id, out var textureRecord))
		{
			return textureRecord.StateTrackingKey != 0 ? textureRecord.StateTrackingKey : handle.Id;
		}

		if (_buffers.TryGetValue(handle.Id, out _))
		{
			return handle.Id;
		}

		throw new InvalidOperationException($"Resource handle {handle.Id} was not registered.");
	}

	internal ResourceState GetResourceStateByTrackingKey(RenderGraphResourceHandle handle, int trackingKey)
	{
		if (trackingKey < 0)
		{
			var slotId = -trackingKey;
			if (_transientSlotStates.TryGetValue(slotId, out var state))
			{
				return state;
			}

			if (_textures.TryGetValue(handle.Id, out var record))
			{
				return record.CurrentState;
			}
		}

		if (_textures.TryGetValue(handle.Id, out var textureRecord))
		{
			return textureRecord.CurrentState;
		}

		if (_buffers.TryGetValue(handle.Id, out var bufferRecord))
		{
			return bufferRecord.CurrentState;
		}

		throw new InvalidOperationException($"Resource handle {handle.Id} was not registered.");
	}

	internal void SetResourceStateByTrackingKey(int trackingKey, ResourceState state)
	{
		if (trackingKey < 0)
		{
			_transientSlotStates[-trackingKey] = state;
			return;
		}

		if (_textures.TryGetValue(trackingKey, out var textureRecord))
		{
			textureRecord.CurrentState = state;
			return;
		}

		if (_buffers.TryGetValue(trackingKey, out var bufferRecord))
		{
			bufferRecord.CurrentState = state;
			return;
		}

		throw new InvalidOperationException($"State tracking key {trackingKey} was not registered.");
	}

	internal IGfxTexture GetTexture(RenderGraphResourceHandle handle)
	{
		if (_textures.TryGetValue(handle.Id, out var record) == false)
		{
			throw new InvalidOperationException($"Texture handle {handle.Id} was not registered.");
		}

		if (record.IsTransient)
		{
			return GetOrCreateTransientTexture(handle, record);
		}

		if (record.Texture is null)
		{
			if (_device is null)
			{
				throw new InvalidOperationException("Render graph device has not been configured.");
			}

			record.Texture = _device.CreateTexture(record.Descriptor);
		}

		return record.Texture;
	}

	internal IGfxBuffer GetBuffer(RenderGraphResourceHandle handle)
	{
		if (_buffers.TryGetValue(handle.Id, out var record) == false)
		{
			throw new InvalidOperationException($"Buffer handle {handle.Id} was not registered.");
		}

		if (record.Buffer is null)
		{
			if (_device is null)
			{
				throw new InvalidOperationException("Render graph device has not been configured.");
			}

			record.Buffer = _device.CreateBuffer(record.Descriptor);
		}

		return record.Buffer;
	}

	internal ResourceState GetResourceState(RenderGraphResourceHandle handle)
	{
		if (_textures.TryGetValue(handle.Id, out var textureRecord))
		{
			if (textureRecord.IsTransient)
			{
				var slotId = textureRecord.TransientSlotId;
				if (_transientSlotStates.TryGetValue(slotId, out var slotState))
				{
					return slotState;
				}
			}

			return textureRecord.CurrentState;
		}

		if (_buffers.TryGetValue(handle.Id, out var bufferRecord))
		{
			return bufferRecord.CurrentState;
		}

		throw new InvalidOperationException($"Resource handle {handle.Id} was not registered.");
	}

	internal void SetResourceState(RenderGraphResourceHandle handle, ResourceState state)
	{
		if (_textures.TryGetValue(handle.Id, out var textureRecord))
		{
			textureRecord.CurrentState = state;
			if (textureRecord.IsTransient)
			{
				_transientSlotStates[textureRecord.TransientSlotId] = state;
			}

			return;
		}

		if (_buffers.TryGetValue(handle.Id, out var bufferRecord))
		{
			bufferRecord.CurrentState = state;
			return;
		}

		throw new InvalidOperationException($"Resource handle {handle.Id} was not registered.");
	}

	internal IGfxResource GetResource(RenderGraphResourceHandle handle)
	{
		if (_textures.TryGetValue(handle.Id, out _))
		{
			return GetTexture(handle);
		}

		if (_buffers.TryGetValue(handle.Id, out _))
		{
			return GetBuffer(handle);
		}

		throw new InvalidOperationException($"Resource handle {handle.Id} was not registered.");
	}

	private IGfxTexture GetOrCreateTransientTexture(RenderGraphResourceHandle handle, TextureRecord record)
	{
		var slotId = record.TransientSlotId != 0 ? record.TransientSlotId : handle.Id;
		if (_activeTransientSlots.TryGetValue(slotId, out var existingSlot))
		{
			record.Texture = existingSlot.Texture;
			record.CurrentState = _transientSlotStates.TryGetValue(slotId, out var slotState)
				? slotState
				: existingSlot.InitialState;
			return existingSlot.Texture;
		}

		var key = new TexturePoolKey(record.Descriptor);
		var state = record.InitialState;
		IGfxTexture texture;
		if (_availableTransientTextures.TryGetValue(key, out var pool) && pool.Count > 0)
		{
			var pooled = pool.Pop();
			if (pool.Count == 0)
			{
				_availableTransientTextures.Remove(key);
			}

			texture = pooled.Texture;
			state = pooled.LastKnownState;
		}
		else
		{
			if (_device is null)
			{
				throw new InvalidOperationException("Render graph device has not been configured.");
			}

			texture = _device.CreateTexture(record.Descriptor);
			Interlocked.Increment(ref _liveTransientTextureCount);
			Interlocked.Add(ref _liveTransientTextureBytes, EstimateTextureBytes(record.Descriptor));
		}

		record.Texture = texture;
		record.CurrentState = state;
		record.TransientSlotId = slotId;
		record.StateTrackingKey = -slotId;
		_activeTransientSlots[slotId] = new ActiveTransientSlot(slotId, key, texture, state, _transientPoolEpoch);
		_transientSlotStates[slotId] = state;
		return texture;
	}

	private void ReclaimFrameRecordsToDevice(
		IGfxDevice? device,
		ITexturePoolDevice? poolDevice,
		GpuSubmissionToken submission)
	{
		// Transient textures go straight back to the pool, while the GPU may still be running the frame that used them.
		// The next frame reuses them rather than allocating a set per frame in flight. That is safe because every frame
		// is recorded to the same queue and executes in order: Metal tracks hazards on these textures (bindless ones
		// are declared with useResource), and D3D12 transitions them from the state the previous frame left them in.
		// Their contents never carry over between frames. Destroying one still waits for the GPU, see LastUse.
		foreach (var slot in _activeTransientSlots.Values)
		{
			var lastKnownState = _transientSlotStates.TryGetValue(slot.SlotId, out var state)
				? state
				: slot.InitialState;
			var entry = new TransientPoolEntry(slot.Texture, lastKnownState, submission, _frameIndex);
			if (slot.PoolEpoch != _transientPoolEpoch)
			{
				RetireTransientTexture(device, entry);
				continue;
			}

			if (_availableTransientTextures.TryGetValue(slot.PoolKey, out var pool) == false)
			{
				pool = new Stack<TransientPoolEntry>();
				_availableTransientTextures[slot.PoolKey] = pool;
			}

			pool.Push(entry);
		}

		foreach (var record in _textures.Values)
		{
			if (record.IsTransient == false && record.Texture is not null && record.OwnsTexture)
			{
				var texture = record.Texture;
				var lastKnownState = record.CurrentState;
				QueueRetirement(
					device,
					submission,
					() =>
					{
						var recycled = poolDevice?.ReturnTexture(texture, lastKnownState) ?? false;
						if (recycled == false)
						{
							DisposeTexture(texture);
						}
					},
					texture.Name ?? "Owned render-graph texture");
			}

			record.Reset();
			_textureRecordPool.Push(record);
		}

		foreach (var record in _buffers.Values)
		{
			if (record.OwnsBuffer && record.Buffer is not null)
			{
				var buffer = record.Buffer;
				if (buffer is IDisposable disposableBuffer)
				{
					QueueRetirement(
						device,
						submission,
						disposableBuffer.Dispose,
						buffer.Name ?? "Owned render-graph buffer");
				}
			}

			record.Reset();
			_bufferRecordPool.Push(record);
		}

		_textures.Clear();
		_buffers.Clear();
		_activeTransientSlots.Clear();
		_transientSlotStates.Clear();
	}

	private static ResourceState DetermineInitialState(TextureUsage usage)
	{
		if ((usage & TextureUsage.RenderTarget) != 0)
		{
			return ResourceState.RenderTarget;
		}

		if ((usage & TextureUsage.DepthStencil) != 0)
		{
			return ResourceState.DepthWrite;
		}

		if ((usage & TextureUsage.UnorderedAccess) != 0)
		{
			return ResourceState.UnorderedAccess;
		}

		if ((usage & TextureUsage.ShaderResource) != 0)
		{
			return ResourceState.ShaderResource;
		}

		return ResourceState.Common;
	}

	private void QueueRetirement(
		IGfxDevice? device,
		GpuSubmissionToken submission,
		Action release,
		string name)
	{
		if (device is null)
		{
			release();
			return;
		}

		if (submission.IsValid)
		{
			device.RetireAfter(submission, release, name);
		}
		else
		{
			device.Retire(release, name);
		}
	}

	private void DisposeTransientTexture(IGfxTexture texture)
	{
		Interlocked.Decrement(ref _liveTransientTextureCount);
		Interlocked.Add(ref _liveTransientTextureBytes, -EstimateTextureBytes(texture.Descriptor));
		DisposeTexture(texture);
	}

	private static void DisposeTexture(IGfxTexture texture)
	{
		if (texture is IDisposable disposableTexture)
		{
			disposableTexture.Dispose();
		}
	}

	private void DisposeAvailableTransientTextures()
	{
		foreach (var pool in _availableTransientTextures.Values)
		{
			while (pool.Count > 0)
			{
				RetireTransientTexture(_device, pool.Pop());
			}
		}

		_availableTransientTextures.Clear();
	}

	/// <summary>Releases pooled textures nothing has reused for <see cref="TransientPoolIdleFrameLimit"/> frames.</summary>
	private void ReleaseIdleTransientTextures()
	{
		foreach (var pool in _availableTransientTextures.Values)
		{
			var hasIdleEntry = false;
			foreach (var entry in pool)
			{
				if (_frameIndex - entry.ReturnedFrame > TransientPoolIdleFrameLimit)
				{
					hasIdleEntry = true;
					break;
				}
			}

			if (hasIdleEntry == false)
			{
				continue;
			}

			// The stack is rebuilt in its original order, so the most recently returned texture stays on top.
			_retainedPoolEntries.Clear();
			while (pool.Count > 0)
			{
				var entry = pool.Pop();
				if (_frameIndex - entry.ReturnedFrame > TransientPoolIdleFrameLimit)
				{
					RetireTransientTexture(_device, entry);
				}
				else
				{
					_retainedPoolEntries.Add(entry);
				}
			}

			for (var i = _retainedPoolEntries.Count - 1; i >= 0; i--)
			{
				pool.Push(_retainedPoolEntries[i]);
			}
		}

		_retainedPoolEntries.Clear();
	}

	private void RetireTransientTexture(IGfxDevice? device, TransientPoolEntry entry)
	{
		var texture = entry.Texture;
		QueueRetirement(
			device,
			entry.LastUse,
			() => DisposeTransientTexture(texture),
			texture.Name ?? "Transient render-graph texture");
	}

	/// <summary>Transient texture memory as of the last completed frame. Safe to read from any thread.</summary>
	public TransientTextureMemory TransientMemory => Volatile.Read(ref _lastFrameTransientMemory);

	private void PublishTransientMemory()
	{
		var transientHandles = 0;
		foreach (var record in _textures.Values)
		{
			if (record.IsTransient)
			{
				transientHandles++;
			}
		}

		var activeBytes = 0L;
		foreach (var slot in _activeTransientSlots.Values)
		{
			activeBytes += EstimateTextureBytes(slot.Texture.Descriptor);
		}

		var pooledCount = 0;
		var pooledBytes = 0L;
		var pooledDescriptors = 0;
		foreach (var pool in _availableTransientTextures.Values)
		{
			if (pool.Count > 0)
			{
				pooledDescriptors++;
			}

			foreach (var entry in pool)
			{
				pooledCount++;
				pooledBytes += EstimateTextureBytes(entry.Texture.Descriptor);
			}
		}

		Volatile.Write(
			ref _lastFrameTransientMemory,
			new TransientTextureMemory(
				transientHandles,
				_activeTransientSlots.Count,
				activeBytes,
				pooledCount,
				pooledBytes,
				Volatile.Read(ref _liveTransientTextureCount),
				Interlocked.Read(ref _liveTransientTextureBytes),
				pooledDescriptors));
	}

	/// <summary>Bytes a texture's mip chain occupies, before any padding or alignment the driver adds.</summary>
	internal static long EstimateTextureBytes(in TextureDescriptor descriptor)
	{
		var total = 0L;
		var mipLevels = Math.Max(descriptor.MipLevels, 1);
		for (var mip = 0; mip < mipLevels; mip++)
		{
			var width = Math.Max(descriptor.Width >> mip, 1);
			var height = Math.Max(descriptor.Height >> mip, 1);
			var depth = descriptor.Dimension == TextureDimension.Texture3D
				? Math.Max(descriptor.Depth >> mip, 1)
				: 1;
			total += TextureFormatUtilities.GetMipDataSize(descriptor.Format, width, height, depth);
		}

		return total;
	}
}

/// <summary>
/// Where the render graph's transient textures are as of one frame. Textures that are neither in use by that frame
/// nor back in the pool are retiring: an earlier frame still in flight on the GPU holds them.
/// </summary>
public sealed record TransientTextureMemory(
	int TransientHandles,
	int ActiveSlots,
	long ActiveBytes,
	int PooledTextures,
	long PooledBytes,
	int LiveTextures,
	long LiveBytes,
	int DistinctDescriptors)
{
	public override string ToString()
	{
		var retiringTextures = LiveTextures - ActiveSlots - PooledTextures;
		var retiringBytes = LiveBytes - ActiveBytes - PooledBytes;

		return
			$"Render graph transients: {TransientHandles} handles in {ActiveSlots} alias slots using {Megabytes(ActiveBytes)} this frame; " +
			$"{LiveTextures} textures alive ({Megabytes(LiveBytes)}): {PooledTextures} pooled ({Megabytes(PooledBytes)}, {DistinctDescriptors} descriptors), " +
			$"{retiringTextures} retiring with frames in flight ({Megabytes(retiringBytes)}).";
	}

	private static string Megabytes(long bytes)
	{
		return $"{bytes / (1024.0 * 1024.0):F1} MB";
	}
}
