#nullable enable
using System.Collections.Generic;
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
		public TextureRecord(TextureDescriptor descriptor, bool ownsTexture, IGfxTexture? texture)
		{
			Descriptor = descriptor;
			OwnsTexture = ownsTexture;
			Texture = texture;
		}

		public TextureDescriptor Descriptor { get; }

		public bool OwnsTexture { get; }

		public IGfxTexture? Texture { get; set; }
	}

	private int _nextHandleId = 1;
	private readonly Dictionary<int, TextureRecord> _textures = new();
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

		_textures.Clear();
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

	public RenderGraphResourceHandle ImportTexture(IGfxTexture texture, bool takeOwnership = false)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		_textures[handle.Id] = new TextureRecord(texture.Descriptor, ownsTexture: takeOwnership, texture);
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
}
