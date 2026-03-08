using System.Collections.Generic;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

internal sealed class EditorSceneRenderTargetManager : IDisposable
{
	private readonly Queue<PendingTextureRelease> _pendingReleases = new();
	private IGfxTexture? _sceneColor;
	private Int2 _size = Int2.Zero;

	private readonly record struct PendingTextureRelease(
		IGfxTexture Texture,
		ulong RetireSubmissionId,
		ResourceState LastKnownState);

	public void Advance(IGfxDevice device)
	{
		RetirePending(device);
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

	public IGfxTexture EnsureTarget(IGfxDevice device, Int2 size)
	{
		RetirePending(device);
		if (_sceneColor is not null && _size == size)
		{
			return _sceneColor;
		}

		ReleaseCurrent(device);
		_size = size;
		_sceneColor = device.CreateTexture(new TextureDescriptor(
			size.X,
			size.Y,
			TextureFormat.Bgra8Unorm,
			TextureUsage.RenderTarget | TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f)));
		return _sceneColor;
	}

	public void Reset()
	{
		ReleaseCurrent(null);
		RetirePending(null);
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

		EnqueueRelease(device, _sceneColor, ResourceState.RenderTarget);
		_sceneColor = null;
	}

	private void EnqueueRelease(IGfxDevice? device, IGfxTexture texture, ResourceState lastKnownState)
	{
		var retireSubmissionId = 0UL;
		if (device is IGpuSubmissionTimeline submissionTimeline)
		{
			retireSubmissionId = submissionTimeline.LastSubmittedId;
		}

		_pendingReleases.Enqueue(new PendingTextureRelease(texture, retireSubmissionId, lastKnownState));
	}

	private void RetirePending(IGfxDevice? device)
	{
		var completedId = ulong.MaxValue;
		ITexturePoolDevice? texturePoolDevice = null;
		if (device is IGpuSubmissionTimeline submissionTimeline)
		{
			submissionTimeline.PumpCompleted();
			completedId = submissionTimeline.CompletedId;
		}

		if (device is ITexturePoolDevice pooledDevice)
		{
			texturePoolDevice = pooledDevice;
		}

		while (_pendingReleases.Count > 0)
		{
			var pending = _pendingReleases.Peek();
			if (pending.RetireSubmissionId > completedId)
			{
				break;
			}

			var pooled = texturePoolDevice?.ReturnTexture(pending.Texture, pending.LastKnownState) ?? false;
			if (pooled == false && pending.Texture is IDisposable disposableTexture)
			{
				disposableTexture.Dispose();
			}

			_pendingReleases.Dequeue();
		}
	}
}
