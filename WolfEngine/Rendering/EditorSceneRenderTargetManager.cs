using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

internal sealed class EditorSceneRenderTargetManager : IDisposable
{
	private IGfxTexture? _sceneColor;
	private Int2 _size = Int2.Zero;
	private ResourceState _sceneColorState = ResourceState.Common;

	public void Advance(IGfxDevice device)
	{
		if (device is IGpuSubmissionTimeline submissionTimeline)
		{
			submissionTimeline.PumpCompleted();
		}
	}

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

	public ResourceState CurrentState => _sceneColorState;

	public IGfxTexture EnsureTarget(IGfxDevice device, Int2 size)
	{
		if (_sceneColor is not null && _size == size)
		{
			return _sceneColor;
		}

		ReleaseCurrent(device);
		_size = size;
		_sceneColor = device.CreateTexture(new TextureDescriptor(
			size.X,
			size.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)));
		_sceneColorState = ResourceState.RenderTarget;
		return _sceneColor;
	}

	public void SetCurrentState(ResourceState state)
	{
		if (_sceneColor is null)
		{
			return;
		}

		_sceneColorState = state;
	}

	public void Reset()
	{
		ReleaseCurrent(null);
		_size = Int2.Zero;
	}

	public void Dispose()
	{
		Reset();
	}

	private void ReleaseCurrent(IGfxDevice? device)
	{
		if (_sceneColor is null)
		{
			return;
		}

		EnqueueRelease(device, _sceneColor, _sceneColorState);
		_sceneColor = null;
		_sceneColorState = ResourceState.Common;
	}

	private void EnqueueRelease(IGfxDevice? device, IGfxTexture texture, ResourceState lastKnownState)
	{
		if (device is null)
		{
			(texture as IDisposable)?.Dispose();
			return;
		}

		// The UI frame being consumed during this render can still reference the old scene texture.
		// Device retirement binds this release to the submission that consumes that UI frame.
		var texturePoolDevice = device as ITexturePoolDevice;
		device.Retire(
			() =>
			{
				var pooled = texturePoolDevice?.ReturnTexture(texture, lastKnownState) ?? false;
				if (pooled == false)
				{
					(texture as IDisposable)?.Dispose();
				}
			},
			texture.Name ?? "Editor scene render target");
	}
}
