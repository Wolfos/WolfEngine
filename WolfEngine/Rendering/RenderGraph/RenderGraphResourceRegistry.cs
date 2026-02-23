#nullable enable
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Central book-keeping for logical render graph resources.
/// Responsible for allocating transient handles and resolving them to real GPU allocations later.
/// </summary>
public sealed class RenderGraphResourceRegistry
{
private sealed class TextureRecord
{
	public TextureDescriptor Descriptor;
	public bool OwnsTexture;
	public IGfxTexture? Texture;
	public ResourceState InitialState;
	public ResourceState CurrentState;

	public void Initialize(TextureDescriptor descriptor, bool ownsTexture, IGfxTexture? texture, ResourceState state)
	{
		Descriptor = descriptor;
		OwnsTexture = ownsTexture;
		Texture = texture;
		InitialState = state;
		CurrentState = state;
	}

	public void Reset()
	{
		Descriptor = default;
		OwnsTexture = false;
		Texture = null;
		InitialState = ResourceState.Common;
		CurrentState = ResourceState.Common;
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

private int _nextHandleId = 1;
private readonly Dictionary<int, TextureRecord> _textures = new();
private readonly Dictionary<int, BufferRecord> _buffers = new();
private readonly Stack<TextureRecord> _texturePool = new();
private readonly Stack<BufferRecord> _bufferPool = new();
private readonly List<PendingTextureRelease> _pendingTextureReleases = new();
private readonly List<PendingBufferRelease> _pendingBufferReleases = new();
private IGfxDevice _device = null!;
private ITexturePoolDevice? _texturePoolDevice;
private IGpuSubmissionTimeline? _submissionTimeline;

private readonly record struct PendingTextureRelease(IGfxTexture Texture, ResourceState LastKnownState, ulong RetireSubmissionId);
private readonly record struct PendingBufferRelease(IGfxBuffer Buffer, ulong RetireSubmissionId);

internal int PendingDeferredReleaseCount => _pendingTextureReleases.Count + _pendingBufferReleases.Count;

public void SetDevice(IGfxDevice device)
{
	_device = device;
	_texturePoolDevice = device as ITexturePoolDevice;
	_submissionTimeline = device as IGpuSubmissionTimeline;
}

public void BeginFrame()
{
	_submissionTimeline?.PumpCompleted();
	RetireDeferredResources();
	var retireSubmissionId = _submissionTimeline?.LastSubmittedId ?? 0;

	foreach (var record in _textures.Values)
	{
		if (record.Texture is not null && record.OwnsTexture)
		{
			_pendingTextureReleases.Add(new PendingTextureRelease(record.Texture, record.CurrentState, retireSubmissionId));
		}

		// Always clear pooled records so external/imported references (e.g. CAMetalDrawable) are released.
		record.Reset();
		_texturePool.Push(record);
	}

	foreach (var record in _buffers.Values)
	{
		if (record.OwnsBuffer && record.Buffer is not null)
		{
			_pendingBufferReleases.Add(new PendingBufferRelease(record.Buffer, retireSubmissionId));
		}

		record.Reset();
		_bufferPool.Push(record);
	}

	_textures.Clear();
	_buffers.Clear();
	_nextHandleId = 1;
	}

	public void EndFrame()
	{
		// Resources remain tracked until the next BeginFrame call, where they are reclaimed.
	}

	public RenderGraphResourceHandle CreateTransientTexture(in TextureDescriptor descriptor)
	{
		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		var record = _texturePool.Count > 0 ? _texturePool.Pop() : new TextureRecord();
		record.Initialize(descriptor, true, null, DetermineInitialState(descriptor.Usage));
		_textures[handle.Id] = record;
		return handle;
	}

	public RenderGraphResourceHandle ImportTexture(IGfxTexture texture, bool takeOwnership = false, ResourceState initialState = ResourceState.Common)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		var record = _texturePool.Count > 0 ? _texturePool.Pop() : new TextureRecord();
		record.Initialize(texture.Descriptor, takeOwnership, texture, initialState);
		_textures[handle.Id] = record;
		return handle;
	}
	

	internal IGfxTexture GetTexture(RenderGraphResourceHandle handle)
	{
		if (_textures.TryGetValue(handle.Id, out var record) == false)
		{
			throw new InvalidOperationException($"Texture handle {handle.Id} was not registered.");
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
		if (_textures.TryGetValue(handle.Id, out var textureRecord))
		{
			return GetTexture(handle);
		}

		if (_buffers.TryGetValue(handle.Id, out var bufferRecord))
		{
			return GetBuffer(handle);
		}

		throw new InvalidOperationException($"Resource handle {handle.Id} was not registered.");
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
		var completedId = _submissionTimeline?.CompletedId ?? ulong.MaxValue;

		for (var i = _pendingTextureReleases.Count - 1; i >= 0; i--)
		{
			var pending = _pendingTextureReleases[i];
			if (pending.RetireSubmissionId > completedId)
			{
				continue;
			}

			var recycled = _texturePoolDevice?.ReturnTexture(pending.Texture, pending.LastKnownState) ?? false;
			if (recycled == false && pending.Texture is IDisposable disposableTexture)
			{
				disposableTexture.Dispose();
			}

			_pendingTextureReleases.RemoveAt(i);
		}

		for (var i = _pendingBufferReleases.Count - 1; i >= 0; i--)
		{
			var pending = _pendingBufferReleases[i];
			if (pending.RetireSubmissionId > completedId)
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
}
