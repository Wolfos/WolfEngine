using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class CpuDataReleaseTests
{
	[Test]
	public void ReleasedMeshGeometryIsReloadedFromItsSource()
	{
		var reloads = 0;
		var mesh = CreateTriangle();
		mesh.CpuGeometrySource = () =>
		{
			reloads++;

			return CreateTriangle();
		};

		mesh.ReleaseCpuGeometryAfterUpload();

		Assert.That(mesh.HasCpuGeometry, Is.False);
		Assert.That(mesh.VertexCount, Is.EqualTo(3));
		Assert.That(reloads, Is.Zero, "the vertex count must not need the geometry");

		Assert.That(mesh.Vertices, Has.Length.EqualTo(3));
		Assert.That(mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
		Assert.That(mesh.Normals, Has.Length.EqualTo(3));
		Assert.That(reloads, Is.EqualTo(1), "a reload is kept for later readers");
	}

	[Test]
	public void MeshWithoutASourceKeepsItsGeometry()
	{
		var mesh = CreateTriangle();

		mesh.ReleaseCpuGeometryAfterUpload();

		Assert.That(mesh.HasCpuGeometry, Is.True);
	}

	[Test]
	public void SkinnedMeshKeepsItsGeometry()
	{
		var mesh = new Mesh(
			[new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0, 1, 2],
			boneIndices: new uint[12],
			boneWeights: new float[12]);
		mesh.CpuGeometrySource = () => throw new AssertionException("a skinned mesh must not be reloaded");

		mesh.ReleaseCpuGeometryAfterUpload();

		Assert.That(mesh.HasCpuGeometry, Is.True);
		Assert.That(mesh.IsSkinned, Is.True);
	}

	[Test]
	public void ReleasedTextureMipsAreReloadedFromTheirSource()
	{
		var texture = CreateTexture();
		texture.MipDataSource = () => CreateTexture().MipLevels;

		texture.ReleaseMipDataAfterUpload();

		Assert.That(texture.HasCpuMipData, Is.False);
		Assert.That(texture.MipCount, Is.EqualTo(1));
		Assert.That(texture.MipLevels[0].Data, Has.Length.EqualTo(16));
		Assert.That(texture.HasCpuMipData, Is.True);
	}

	[Test]
	public void ReplacedTextureDataIsNotReleased()
	{
		var texture = CreateTexture();
		texture.MipDataSource = () => throw new AssertionException("replaced data must not come from the old source");

		texture.ApplyTextureData(2, 2, false, TextureFormat.Rgba8Unorm, [new TextureMipData(2, 2, new byte[16])]);
		texture.ReleaseMipDataAfterUpload();

		Assert.That(texture.HasCpuMipData, Is.True);
	}

	private static Mesh CreateTriangle()
	{
		return new Mesh(
			[new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0, 1, 2]);
	}

	private static Texture CreateTexture()
	{
		return new Texture("test", 2, 2, false, TextureFormat.Rgba8Unorm, [new TextureMipData(2, 2, new byte[16])]);
	}
}
