#nullable enable

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
			Format = descriptor.Format;
			Usage = descriptor.Usage;
			ClearColor = descriptor.ClearColor;
			DepthClear = descriptor.DepthClear;
		}

		public int Width { get; }
		public int Height { get; }
		public TextureFormat Format { get; }
		public TextureUsage Usage { get; }
		public System.Numerics.Vector4 ClearColor { get; }
		public float DepthClear { get; }

		public bool Equals(TexturePoolKey other)
		{
			return Width == other.Width &&
			       Height == other.Height &&
			       Format == other.Format &&
			       Usage == other.Usage &&
			       ClearColor.Equals(other.ClearColor) &&
			       DepthClear.Equals(other.DepthClear);
		}

		public override bool Equals(object? obj) => obj is TexturePoolKey other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(Width, Height, Format, Usage, ClearColor, DepthClear);
	}

	private readonly struct TransientPoolEntry
	{
		public TransientPoolEntry(IGfxTexture texture, ResourceState lastKnownState)
		{
			Texture = texture;
			LastKnownState = lastKnownState;
		}

		public IGfxTexture Texture { get; }
		public ResourceState LastKnownState { get; }
	}

	private readonly struct ActiveTransientSlot
	{
		public ActiveTransientSlot(int slotId, TexturePoolKey poolKey, IGfxTexture texture, ResourceState initialState)
		{
			SlotId = slotId;
			PoolKey = poolKey;
			Texture = texture;
			InitialState = initialState;
		}

		public int SlotId { get; }
		public TexturePoolKey PoolKey { get; }
		public IGfxTexture Texture { get; }
		public ResourceState InitialState { get; }
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

	private readonly record struct PendingTransientRecycle(
		IGfxTexture Texture,
		TexturePoolKey Key,
		ResourceState LastKnownState,
		ulong RetireSubmissionId,
		IGpuSubmissionTimeline? Timeline,
		int PoolEpoch);

	private readonly record struct PendingTextureRelease(
		IGfxTexture Texture,
		ResourceState LastKnownState,
		ulong RetireSubmissionId,
		IGpuSubmissionTimeline? Timeline,
		ITexturePoolDevice? PoolDevice);

	private readonly record struct PendingBufferRelease(
		IGfxBuffer Buffer,
		ulong RetireSubmissionId,
		IGpuSubmissionTimeline? Timeline);

	private int _nextHandleId = 1;
	private int _transientPoolEpoch = 1;
	private readonly Dictionary<int, TextureRecord> _textures = new();
	private readonly Dictionary<int, BufferRecord> _buffers = new();
	private readonly Stack<TextureRecord> _textureRecordPool = new();
	private readonly Stack<BufferRecord> _bufferRecordPool = new();
	private readonly Dictionary<int, ActiveTransientSlot> _activeTransientSlots = new();
	private readonly Dictionary<int, ResourceState> _transientSlotStates = new();
	private readonly Dictionary<TexturePoolKey, Stack<TransientPoolEntry>> _availableTransientTextures = new();
	private readonly List<PendingTransientRecycle> _pendingTransientRecycles = new();
	private readonly List<PendingTextureRelease> _pendingTextureReleases = new();
	private readonly List<PendingBufferRelease> _pendingBufferReleases = new();
	private IGfxDevice? _device;
	private ITexturePoolDevice? _texturePoolDevice;
	private IGpuSubmissionTimeline? _submissionTimeline;

	internal int PendingDeferredReleaseCount =>
		_pendingTransientRecycles.Count + _pendingTextureReleases.Count + _pendingBufferReleases.Count;

	public void SetDevice(IGfxDevice device)
	{
		if (device is null)
		{
			throw new ArgumentNullException(nameof(device));
		}

		if (ReferenceEquals(_device, device))
		{
			_texturePoolDevice = device as ITexturePoolDevice;
			_submissionTimeline = device as IGpuSubmissionTimeline;
			return;
		}

		if (_device is not null)
		{
			ReclaimFrameRecordsToPending(_submissionTimeline, _texturePoolDevice);
			InvalidateTransientTexturePool();
			RetireDeferredResources();
		}

		_device = device;
		_texturePoolDevice = device as ITexturePoolDevice;
		_submissionTimeline = device as IGpuSubmissionTimeline;
	}

	public void BeginFrame()
	{
		ReclaimFrameRecordsToPending(_submissionTimeline, _texturePoolDevice);
		RetireDeferredResources();
		_nextHandleId = 1;
	}

	public void EndFrame()
	{
		// Resources remain tracked until the next BeginFrame call, where they are reclaimed.
	}

	public void InvalidateTransientTexturePool()
	{
		_transientPoolEpoch++;
		DisposeAvailableTransientTextures();
	}

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

	internal void AssignTransientTextureSlots(IReadOnlyDictionary<int, int> handleToSlotAssignments)
	{
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
				: handleId;
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
		}

		record.Texture = texture;
		record.CurrentState = state;
		record.TransientSlotId = slotId;
		record.StateTrackingKey = -slotId;
		_activeTransientSlots[slotId] = new ActiveTransientSlot(slotId, key, texture, state);
		_transientSlotStates[slotId] = state;
		return texture;
	}

	private void ReclaimFrameRecordsToPending(IGpuSubmissionTimeline? timeline, ITexturePoolDevice? poolDevice)
	{
		var retireSubmissionId = timeline?.LastSubmittedId ?? 0;

		foreach (var slot in _activeTransientSlots.Values)
		{
			var lastKnownState = _transientSlotStates.TryGetValue(slot.SlotId, out var state)
				? state
				: slot.InitialState;
			_pendingTransientRecycles.Add(new PendingTransientRecycle(
				slot.Texture,
				slot.PoolKey,
				lastKnownState,
				retireSubmissionId,
				timeline,
				_transientPoolEpoch));
		}

		foreach (var record in _textures.Values)
		{
			if (record.IsTransient == false && record.Texture is not null && record.OwnsTexture)
			{
				_pendingTextureReleases.Add(new PendingTextureRelease(
					record.Texture,
					record.CurrentState,
					retireSubmissionId,
					timeline,
					poolDevice));
			}

			record.Reset();
			_textureRecordPool.Push(record);
		}

		foreach (var record in _buffers.Values)
		{
			if (record.OwnsBuffer && record.Buffer is not null)
			{
				_pendingBufferReleases.Add(new PendingBufferRelease(record.Buffer, retireSubmissionId, timeline));
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

	private void RetireDeferredResources()
	{
		for (var i = _pendingTransientRecycles.Count - 1; i >= 0; i--)
		{
			var pending = _pendingTransientRecycles[i];
			if (IsSubmissionCompleted(pending.RetireSubmissionId, pending.Timeline) == false)
			{
				continue;
			}

			if (pending.PoolEpoch == _transientPoolEpoch)
			{
				if (_availableTransientTextures.TryGetValue(pending.Key, out var pool) == false)
				{
					pool = new Stack<TransientPoolEntry>();
					_availableTransientTextures[pending.Key] = pool;
				}

				pool.Push(new TransientPoolEntry(pending.Texture, pending.LastKnownState));
			}
			else
			{
				DisposeTexture(pending.Texture);
			}

			_pendingTransientRecycles.RemoveAt(i);
		}

		for (var i = _pendingTextureReleases.Count - 1; i >= 0; i--)
		{
			var pending = _pendingTextureReleases[i];
			if (IsSubmissionCompleted(pending.RetireSubmissionId, pending.Timeline) == false)
			{
				continue;
			}

			var recycled = pending.PoolDevice?.ReturnTexture(pending.Texture, pending.LastKnownState) ?? false;
			if (recycled == false)
			{
				DisposeTexture(pending.Texture);
			}

			_pendingTextureReleases.RemoveAt(i);
		}

		for (var i = _pendingBufferReleases.Count - 1; i >= 0; i--)
		{
			var pending = _pendingBufferReleases[i];
			if (IsSubmissionCompleted(pending.RetireSubmissionId, pending.Timeline) == false)
			{
				continue;
			}

			if (pending.Buffer is IDisposable disposableBuffer)
			{
				disposableBuffer.Dispose();
			}

			_pendingBufferReleases.RemoveAt(i);
		}
	}

	private static bool IsSubmissionCompleted(ulong retireSubmissionId, IGpuSubmissionTimeline? timeline)
	{
		if (timeline is null)
		{
			return true;
		}

		timeline.PumpCompleted();
		return retireSubmissionId <= timeline.CompletedId;
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
				DisposeTexture(pool.Pop().Texture);
			}
		}

		_availableTransientTextures.Clear();
	}
}
