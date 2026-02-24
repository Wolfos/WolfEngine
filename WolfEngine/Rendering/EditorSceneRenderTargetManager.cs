using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

internal sealed class EditorSceneRenderTargetManager : IDisposable
{
	private IGfxTexture? _sceneColor;
	private Int2 _size = Int2.Zero;

	public bool TryGetCurrent(out IGfxTexture texture, out nint textureId, out Int2 size)
	{
		size = _size;
		if (_sceneColor is null || _sceneColor.ShaderResourceView.IsValid == false)
		{
			texture = null!;
			textureId = 0;
			return false;
		}

		texture = _sceneColor;
		textureId = (nint)_sceneColor.ShaderResourceView.Value;
		return true;
	}

	public IGfxTexture EnsureTarget(IGfxDevice device, Int2 size)
	{
		if (_sceneColor is not null && _size == size)
		{
			return _sceneColor;
		}

		DisposeTexture();
		_size = size;
		_sceneColor = device.CreateTexture(new TextureDescriptor(
			size.X,
			size.Y,
			TextureFormat.Bgra8Unorm,
			TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new Vector4(0.05f, 0.05f, 0.05f, 1.0f)));
		return _sceneColor;
	}

	public void Reset()
	{
		DisposeTexture();
		_size = Int2.Zero;
	}

	public void Dispose()
	{
		Reset();
	}

	private void DisposeTexture()
	{
		if (_sceneColor is IDisposable disposable)
		{
			disposable.Dispose();
		}

		_sceneColor = null;
	}
}
