#nullable enable
using System.Collections.Generic;

namespace WolfEngine.Rendering;

/// <summary>
/// Central book-keeping for logical render graph resources.
/// Responsible for allocating transient handles and resolving them to real GPU allocations later.
/// </summary>
public sealed class RenderGraphResourceRegistry
{
	private sealed class TextureRecord
	{
		public TextureRecord(TextureDescriptor descriptor, bool isExternal, IRenderGraphTexture? texture)
		{
			Descriptor = descriptor;
			IsExternal = isExternal;
			Texture = texture;
		}

		public TextureDescriptor Descriptor { get; }

		public bool IsExternal { get; }

		public IRenderGraphTexture? Texture { get; set; }
	}

	private int _nextHandleId = 1;
	private readonly Dictionary<int, TextureRecord> _textures = new();
	private IRenderGraphBackend _backend = null!;

	public void SetBackend(IRenderGraphBackend backend)
	{
		_backend = backend ?? throw new ArgumentNullException(nameof(backend));
	}

	public void BeginFrame()
	{
		foreach (var (_, record) in _textures)
		{
			if (record.IsExternal)
			{
				continue;
			}

			record.Texture?.Dispose();
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
		_textures[handle.Id] = new TextureRecord(descriptor, isExternal: false, texture: null);
		return handle;
	}

	public RenderGraphResourceHandle ImportTexture(IRenderGraphTexture texture)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		var handle = new RenderGraphResourceHandle(_nextHandleId++);
		_textures[handle.Id] = new TextureRecord(texture.Descriptor, isExternal: true, texture);
		return handle;
	}

	internal IRenderGraphTexture GetTexture(RenderGraphResourceHandle handle)
	{
		if (_textures.TryGetValue(handle.Id, out var record) == false)
		{
			throw new InvalidOperationException($"Texture handle {handle.Id} was not registered.");
		}

		if (record.Texture is null)
		{
			if (_backend is null)
			{
				throw new InvalidOperationException("Render graph backend has not been configured.");
			}

			record.Texture = _backend.CreateTexture(record.Descriptor);
		}

		return record.Texture;
	}
}
