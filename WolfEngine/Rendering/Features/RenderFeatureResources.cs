using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Features;

/// <summary>
/// Creates a render feature's own GPU resources on the render thread. The engine owns them: they are released
/// when the feature is unregistered, after the last frame that used them has left the GPU.
/// </summary>
public sealed class RenderFeatureResources
{
	private readonly IGfxDevice _device;
	private readonly string _owner;
	private readonly List<RenderFeatureTexture> _textures = [];

	internal RenderFeatureResources(IGfxDevice device, string owner)
	{
		_device = device;
		_owner = owner;
	}

	internal IReadOnlyList<RenderFeatureTexture> Textures => _textures;

	/// <summary>
	/// Creates a 2D texture that passes can sample and that <see cref="RenderFeatureContext.Upload"/> can fill.
	/// With <paramref name="writable"/>, kernels can also write it through <see cref="RenderFeatureComputePass.ReadWrite"/>.
	/// </summary>
	/// <param name="name">Names the texture in the log when it is released.</param>
	public RenderFeatureTexture CreateTexture(string name, int width, int height, TextureFormat format, bool writable = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		if (TextureFormatUtilities.IsCompressed(format) || format is TextureFormat.Unknown or TextureFormat.D32Float)
		{
			throw new ArgumentException($"Render feature textures must use an uncompressed colour format, not {format}.", nameof(format));
		}

		var usage = TextureUsage.ShaderResource | (writable ? TextureUsage.UnorderedAccess : TextureUsage.None);
		var texture = new RenderFeatureTexture(_device.CreateTexture(new TextureDescriptor(width, height, format, usage)), name)
		{
			// The state D3D12 creates it in. The render graph's first barrier starts here, so it must match.
			State = writable ? ResourceState.UnorderedAccess : ResourceState.Common
		};
		_textures.Add(texture);
		return texture;
	}

	/// <summary>Queues every texture for release once the GPU has finished the frames that use them.</summary>
	internal void Release()
	{
		foreach (var texture in _textures)
		{
			if (texture.Owned is IDisposable disposable)
			{
				_device.Retire(disposable.Dispose, $"{_owner} {texture.Name}");
			}
		}

		_textures.Clear();
	}
}
