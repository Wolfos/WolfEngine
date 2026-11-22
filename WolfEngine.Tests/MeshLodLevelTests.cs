using System.Numerics;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class MeshLodLevelTests
{
	[Test]
	public void MeshWithoutLevels_IsItsOnlyLevel()
	{
		var mesh = CreateTriangle();

		Assert.That(mesh.LodLevels, Is.Empty);
		Assert.That(mesh.LodLevelCount, Is.EqualTo(1));
	}

	[Test]
	public void SetLodLevels_KeepsLevelsFinestFirst()
	{
		var mesh = CreateTriangle();
		var levelOne = CreateTriangle();
		var levelTwo = CreateTriangle();

		mesh.SetLodLevels([levelOne, levelTwo]);

		Assert.That(mesh.LodLevels, Is.EqualTo(new[] { levelOne, levelTwo }));
		Assert.That(mesh.LodLevelCount, Is.EqualTo(3));
	}

	[Test]
	public void SetLodLevels_RejectsTheMeshItself()
	{
		var mesh = CreateTriangle();

		Assert.Throws<ArgumentException>(() => mesh.SetLodLevels([mesh]));
	}

	[Test]
	public void SetLodLevels_RejectsALevelWithLevelsOfItsOwn()
	{
		var mesh = CreateTriangle();
		var level = CreateTriangle();
		level.SetLodLevels([CreateTriangle()]);

		Assert.Throws<ArgumentException>(() => mesh.SetLodLevels([level]));
	}

	[Test]
	public void SetLodLevels_RejectsALevelThatDiffersInSkinning()
	{
		var mesh = CreateTriangle();
		var skinned = new Mesh(
			[new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0u, 1u, 2u],
			boneIndices: new uint[12],
			boneWeights: Enumerable.Repeat(0.25f, 12).ToArray());

		Assert.Throws<ArgumentException>(() => mesh.SetLodLevels([skinned]));
	}

	private static Mesh CreateTriangle()
	{
		return new Mesh(
			[new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0u, 1u, 2u]);
	}
}
