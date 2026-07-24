using System;
using System.Collections.Generic;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

internal sealed class EditorSceneRenderTargetManager : IDisposable
{
	private readonly Queue<UnsealedTextureRelease> _unsealedReleases = new();
	private readonly Queue<PendingTextureRelease> _pendingReleases = new();
	private IGfxTexture? _sceneColor;
	private Int2 _size = Int2.Zero;
	private ResourceState _sceneColorState = ResourceState.Common;

	private readonly record struct UnsealedTextureRelease(
		IGfxTexture Texture,
		ResourceState LastKnownState);

	private readonly record struct PendingTextureRelease(
		IGfxTexture Texture,
		ulong RetireSubmissionId,
		ResourceState LastKnownState);

	public void Advance(IGfxDevice device)
	{
		SealReleases(device);
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

	public ResourceState CurrentState => _sceneColorState;

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

		EnqueueRelease(device, _sceneColor, _sceneColorState);
		_sceneColor = null;
		_sceneColorState = ResourceState.Common;
	}

	private void EnqueueRelease(IGfxDevice? device, IGfxTexture texture, ResourceState lastKnownState)
	{
		if (device is null)
		{
			_pendingReleases.Enqueue(new PendingTextureRelease(texture, 0UL, lastKnownState));
			return;
		}

		// The UI frame being consumed during this render can still reference the old scene texture.
		// Defer selecting its retirement fence until the next frame, after that UI work was submitted.
		_unsealedReleases.Enqueue(new UnsealedTextureRelease(texture, lastKnownState));
	}

	private void SealReleases(IGfxDevice device)
	{
		var retireSubmissionId = device is IGpuSubmissionTimeline submissionTimeline
			? submissionTimeline.LastSubmittedId
			: 0UL;

		while (_unsealedReleases.Count > 0)
		{
			var release = _unsealedReleases.Dequeue();
			_pendingReleases.Enqueue(new PendingTextureRelease(
				release.Texture,
				retireSubmissionId,
				release.LastKnownState));
		}
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

		if (device is not null)
		{
			return;
		}

		while (_unsealedReleases.Count > 0)
		{
			var release = _unsealedReleases.Dequeue();
			if (release.Texture is IDisposable disposableTexture)
			{
				disposableTexture.Dispose();
			}
		}
	}
}
