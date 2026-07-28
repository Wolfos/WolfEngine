using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

public sealed class TextureResourceUpdateTests
{
	[Test]
	public void ContentOnlyChange_PreservesCompatibleD3D12Resource()
	{
		var texture = CreateTexture(8, 8, TextureFormat.Rgba8Unorm, mipCount: 4);
		var descriptor = new TextureDescriptor(
			8,
			8,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			mipLevels: 4);

		Assert.That(WolfRendererD3D.CanUpdateTextureResources(texture, in descriptor), Is.True);
	}

	[TestCase(16, 8, TextureFormat.Rgba8Unorm, 4)]
	[TestCase(8, 16, TextureFormat.Rgba8Unorm, 4)]
	[TestCase(8, 8, TextureFormat.R16Unorm, 4)]
	[TestCase(8, 8, TextureFormat.Rgba8Unorm, 1)]
	public void StructuralChange_ReplacesD3D12Resource(
		int width,
		int height,
		TextureFormat format,
		int mipCount)
	{
		var texture = CreateTexture(8, 8, TextureFormat.Rgba8Unorm, mipCount: 4);
		var descriptor = new TextureDescriptor(
			width,
			height,
			format,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			mipLevels: mipCount);

		Assert.That(WolfRendererD3D.CanUpdateTextureResources(texture, in descriptor), Is.False);
	}

	[Test]
	public void UsageChange_ReplacesD3D12Resource()
	{
		var texture = CreateTexture(8, 8, TextureFormat.Rgba8Unorm, mipCount: 4);
		var descriptor = new TextureDescriptor(
			8,
			8,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource,
			mipLevels: 4);

		Assert.That(WolfRendererD3D.CanUpdateTextureResources(texture, in descriptor), Is.False);
	}

	private static Texture CreateTexture(int width, int height, TextureFormat format, int mipCount)
	{
		var mips = new TextureMipData[mipCount];
		var mipWidth = width;
		var mipHeight = height;
		for (var mipIndex = 0; mipIndex < mipCount; mipIndex++)
		{
			mips[mipIndex] = new TextureMipData(
				mipWidth,
				mipHeight,
				new byte[TextureFormatUtilities.GetMipDataSize(format, mipWidth, mipHeight)]);
			mipWidth = Math.Max(1, mipWidth / 2);
			mipHeight = Math.Max(1, mipHeight / 2);
		}

		return new Texture("test", width, height, false, format, mips);
	}
}
