using WolfEngine.Rendering;

namespace WolfEngine.Tests;

public sealed class CubeTextureTests
{
	[Test]
	public void Bc6h_IsSixteenBytesPerFourByFourBlock()
	{
		Assert.That(TextureFormatUtilities.IsCompressed(TextureFormat.Bc6hUfloat), Is.True);
		Assert.That(TextureFormatUtilities.GetBlockWidth(TextureFormat.Bc6hUfloat), Is.EqualTo(4));
		Assert.That(TextureFormatUtilities.GetBlockHeight(TextureFormat.Bc6hUfloat), Is.EqualTo(4));
		Assert.That(TextureFormatUtilities.GetMipDataSize(TextureFormat.Bc6hUfloat, 8, 8), Is.EqualTo(64));
		Assert.That(TextureFormatUtilities.GetMipDataSize(TextureFormat.Bc6hUfloat, 1, 1), Is.EqualTo(16));
		Assert.That(TextureFormatUtilities.GetBytesPerRow(TextureFormat.Bc6hUfloat, 6), Is.EqualTo(32));
	}

	[Test]
	public void CubeFacesKeepTheirCountAtEveryMip_WhileVolumeDepthHalves()
	{
		Assert.That(TextureFormatUtilities.GetMipSliceCount(TextureDimension.Cube, 6, 0), Is.EqualTo(6));
		Assert.That(TextureFormatUtilities.GetMipSliceCount(TextureDimension.Cube, 6, 5), Is.EqualTo(6));
		Assert.That(TextureFormatUtilities.GetMipSliceCount(TextureDimension.Texture3D, 8, 2), Is.EqualTo(2));
		Assert.That(TextureFormatUtilities.GetMipSliceCount(TextureDimension.Texture3D, 8, 5), Is.EqualTo(1));
		Assert.That(TextureFormatUtilities.GetMipSliceCount(TextureDimension.Texture2D, 1, 3), Is.EqualTo(1));
		Assert.That(TextureFormatUtilities.GetArrayLayerCount(TextureDimension.Cube), Is.EqualTo(6));
		Assert.That(TextureFormatUtilities.GetArrayLayerCount(TextureDimension.Texture3D), Is.EqualTo(1));
	}

	[Test]
	public void CubeDescriptor_RequiresSixSquareShaderResourceFaces()
	{
		Assert.DoesNotThrow(() => new TextureDescriptor(8, 8, TextureFormat.Bc6hUfloat, TextureUsage.ShaderResource,
			mipLevels: 4, dimension: TextureDimension.Cube, depth: 6));
		Assert.Throws<ArgumentOutOfRangeException>(() => new TextureDescriptor(8, 8, TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource, dimension: TextureDimension.Cube, depth: 1));
		Assert.Throws<ArgumentException>(() => new TextureDescriptor(8, 4, TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource, dimension: TextureDimension.Cube, depth: 6));
		Assert.Throws<ArgumentException>(() => new TextureDescriptor(8, 8, TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess, dimension: TextureDimension.Cube, depth: 6));
		Assert.Throws<ArgumentException>(() => new TextureDescriptor(8, 8, TextureFormat.Rgba16Float,
			TextureUsage.RenderTarget, dimension: TextureDimension.Cube, depth: 6));
	}

	[Test]
	public void CreateCube_ProducesSixFaceTexture_ThatD3D12UpdatesInPlaceOnlyWhileCubeShaped()
	{
		var faceBytes = TextureFormatUtilities.GetMipDataSize(TextureFormat.Rgba16Float, 4, 4);
		var cube = Texture.CreateCube("cube", 4, false, TextureFormat.Rgba16Float,
			[new TextureMipData(4, 4, new byte[faceBytes * 6], 6)]);
		var matching = new TextureDescriptor(4, 4, TextureFormat.Rgba16Float, TextureUsage.ShaderResource,
			dimension: TextureDimension.Cube, depth: 6);
		var flat = new TextureDescriptor(4, 4, TextureFormat.Rgba16Float, TextureUsage.ShaderResource);

		Assert.That(cube.Dimension, Is.EqualTo(TextureDimension.Cube));
		Assert.That(cube.Depth, Is.EqualTo(6));
		Assert.That(WolfRendererD3D.CanUpdateTextureResources(cube, in matching), Is.True);
		Assert.That(WolfRendererD3D.CanUpdateTextureResources(cube, in flat), Is.False);
	}

	[Test]
	public void CreateCube_RejectsNonSquareFaces()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => Texture.CreateCube("cube", 0, false, TextureFormat.Rgba16Float,
			[new TextureMipData(1, 1, new byte[48], 6)]));
	}
}
