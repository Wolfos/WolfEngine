using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Features;

/// <summary>
/// A texture a render feature's passes read or write: one the feature owns, or one of the view's frame textures
/// that <see cref="RenderFeatureContext"/> exposes.
/// </summary>
public sealed class RenderFeatureTexture
{
	private readonly IGfxTexture? _owned;
	private byte[]? _pendingUpload;
	private int _pendingUploadLength;

	internal RenderFeatureTexture(IGfxTexture owned, string name)
	{
		_owned = owned;
		Name = name;
		Size = new Int2(owned.Descriptor.Width, owned.Descriptor.Height);
		Format = owned.Descriptor.Format;
	}

	internal RenderFeatureTexture(bool isDepth)
	{
		IsDepth = isDepth;
		Name = isDepth ? "Depth" : "Scene Color";
	}

	public string Name { get; }

	public Int2 Size { get; private set; }

	public TextureFormat Format { get; private set; }

	internal bool IsDepth { get; }

	internal IGfxTexture? Owned => _owned;

	/// <summary>The owned texture's state between frames; the render graph tracks it within one.</summary>
	internal ResourceState State { get; set; } = ResourceState.Common;

	/// <summary>This frame's graph handle; invalid until the texture is first used in the frame.</summary>
	internal RenderGraphResourceHandle Handle { get; set; }

	internal void BindFrameTexture(RenderGraphResourceHandle handle, Int2 size, TextureFormat format)
	{
		Handle = handle;
		Size = size;
		Format = format;
	}

	/// <summary>Copies texels now, so the caller's data may change before the upload pass executes.</summary>
	internal ReadOnlyMemory<byte> StageUpload(ReadOnlySpan<byte> texels)
	{
		if (_pendingUpload is null || _pendingUpload.Length < texels.Length)
		{
			_pendingUpload = new byte[texels.Length];
		}

		texels.CopyTo(_pendingUpload);
		_pendingUploadLength = texels.Length;
		return _pendingUpload.AsMemory(0, _pendingUploadLength);
	}
}
