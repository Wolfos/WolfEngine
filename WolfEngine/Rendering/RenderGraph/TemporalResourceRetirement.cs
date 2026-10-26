using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Releases the persistent textures and buffers a view carries between frames. Each release goes through the
/// device's retirement queue, so a resource outlives any submission still referencing it, and a texture goes
/// back to the device's pool in the state it was last left in.
/// </summary>
internal static class TemporalResourceRetirement
{
	public static void RetireTexture(IGfxDevice? device, IGfxTexture texture, ResourceState lastKnownState)
	{
		if (device is null)
		{
			(texture as IDisposable)?.Dispose();
			return;
		}

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
			texture.Name ?? "Temporal render-graph texture");
	}

	public static void RetireBuffer(IGfxDevice? device, IGfxBuffer buffer)
	{
		if (buffer is not IDisposable disposableBuffer)
		{
			return;
		}

		if (device is null)
		{
			disposableBuffer.Dispose();
			return;
		}

		device.Retire(disposableBuffer, buffer.Name ?? "Temporal render-graph buffer");
	}
}
