using System.Text;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class TextureArtifactCompatibilityTests
{
	[Test]
	public void TextureFormatIdsRemainCompatibleWithExistingArtifacts()
	{
		Assert.Multiple(() =>
		{
			Assert.That((int)TextureFormat.D32Float, Is.EqualTo(8));
			Assert.That((int)TextureFormat.Bc3Unorm, Is.EqualTo(9));
			Assert.That((int)TextureFormat.Bc5Unorm, Is.EqualTo(10));
			Assert.That((int)TextureFormat.Bc7Unorm, Is.EqualTo(11));
			Assert.That((int)TextureFormat.Astc4x4Unorm, Is.EqualTo(12));
			Assert.That((int)TextureFormat.Bc1Unorm, Is.EqualTo(13));
			Assert.That((int)TextureFormat.Bc4Unorm, Is.EqualTo(14));
			Assert.That((int)TextureFormat.R32Uint, Is.EqualTo(15));
		});
	}

	[Test]
	public void Read_ExistingBc1Artifact_DoesNotBecomeAstc()
	{
		using var stream = new MemoryStream();
		using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write("WETX"u8);
			writer.Write(TextureArtifactSerializer.CurrentVersion);
			writer.Write(4);
			writer.Write(4);
			writer.Write((byte)1);
			writer.Write(1); // BaseColor semantic; format compatibility is the concern here.
			writer.Write(13); // BC1's serialized ID before R32Uint was added.
			writer.Write((int)TextureCompressionFamily.Bc);
			writer.Write(1);
			writer.Write(4);
			writer.Write(4);
			writer.Write(8);
			writer.Write(new byte[8]);
		}
		stream.Position = 0;

		var texture = TextureArtifactSerializer.Read(stream, "legacy-bc1");

		Assert.That(texture.Format, Is.EqualTo(TextureFormat.Bc1Unorm));
	}
}
