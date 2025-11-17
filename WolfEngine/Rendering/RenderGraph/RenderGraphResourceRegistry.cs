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
		public TextureRecord(TextureDescriptor descriptor, bool ownsTexture, IGfxTexture? texture, ResourceState initialState = ResourceState.Common)
		{
			Descriptor = descriptor;
			OwnsTexture = ownsTexture;
			Texture = texture;
			InitialState = initialState;
			CurrentState = initialState;
		}

		public TextureDescriptor Descriptor { get; }

		public bool OwnsTexture { get; }

		public IGfxTexture? Texture { get; set; }
		
		public ResourceState InitialState { get; }
		
		public ResourceState CurrentState { get; set; }
	}

	private sealed class BufferRecord
	{
		public BufferRecord(BufferDescriptor descriptor, bool ownsBuffer, IGfxBuffer? buffer, ResourceState initialState = ResourceState.Common)
		{
			Descriptor = descriptor;
			OwnsBuffer = ownsBuffer;
			Buffer = buffer;
			InitialState = initialState;
			CurrentState = initialState;
		}

		public BufferDescriptor Descriptor { get; }

		public bool OwnsBuffer { get; }

		public IGfxBuffer? Buffer { get; set; }
		
		public ResourceState InitialState { get; }
		
		public ResourceState CurrentState { get; set; }
	}

	private int _nextHandleId = 1;
	private readonly Dictionary<int, TextureRecord> _textures = new();
	private readonly Dictionary<int, BufferRecord> _buffers = new();
	private IGfxDevice _device = null!;

	public void SetDevice(IGfxDevice device)
	{
		_device = device;
	}

	public void BeginFrame()
	{
		foreach (var (_, record) in _textures)
		{
			if (record.OwnsTexture == false)
			{
				continue;
			}

			if (record.Texture is IDisposable disposable)
			{
				disposable.Dispose();
			}
		}
		
		foreach (var (_, record) in _buffers)
		{
			if (record.OwnsBuffer == false)
			{
				continue;
			}

			if (record.Buffer is IDisposable disposable)
			{
				disposable.Dispose();
			}
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
		_textures[handle.Id] = new TextureRecord(descriptor, ownsTexture: true, texture: null);
		return handle;
	}

	public RenderGraphResourceHandle ImportTexture(IGfxTexture texture, bool takeOwnership = false, ResourceState initialState = ResourceState.Common)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		_textures[handle.Id] = new TextureRecord(texture.Descriptor, ownsTexture: takeOwnership, texture, initialState);
		return handle;
	}

	public RenderGraphResourceHandle CreateTransientBuffer(in BufferDescriptor descriptor)
	{
		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		_buffers[handle.Id] = new BufferRecord(descriptor, ownsBuffer: true, buffer: null);
		return handle;
	}

	public RenderGraphResourceHandle ImportBuffer(IGfxBuffer buffer, bool takeOwnership = false, ResourceState initialState = ResourceState.Common)
	{
		if (buffer is null)
		{
			throw new ArgumentNullException(nameof(buffer));
		}

		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		_buffers[handle.Id] = new BufferRecord(buffer.Descriptor, ownsBuffer: takeOwnership, buffer, initialState);
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
}
