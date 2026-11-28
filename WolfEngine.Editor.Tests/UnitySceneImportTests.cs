using System.Numerics;
using WolfEngine.Editor.UnityImport;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class UnitySceneImportTests
{
	[Test]
	public void PackLayerMaps_KeepsTheFourHeaviestLayersAsIndexPlusOne()
	{
		// One texel, six layers: weights 10, 0, 90, 40, 60, 50. The four heaviest are layers 2, 4, 5 and 3.
		byte[] alphamaps = [10, 0, 90, 40, 60, 50];

		var (indices, weights) = UnityTerrainConversion.PackLayerMaps(alphamaps, 1, 6, out var discarded);

		Assert.Multiple(() =>
		{
			Assert.That(indices, Is.EqualTo(new byte[] { 3, 5, 6, 4 }));
			Assert.That(weights.Sum(weight => weight), Is.EqualTo(255));
			Assert.That(weights[0], Is.GreaterThan(weights[1]));
			Assert.That(weights[1], Is.GreaterThan(weights[2]));
			Assert.That(weights[2], Is.GreaterThan(weights[3]));
			Assert.That(discarded, Is.EqualTo(10.0f / 250.0f).Within(0.0001f));
		});
	}

	[Test]
	public void PackLayerMaps_ReadsLayerPlanesInRowOrder()
	{
		// Two by two texels, two layers. Texel 1 is all layer 1; the others are all layer 0.
		byte[] alphamaps =
		[
			255, 0, 255, 255,
			0, 255, 0, 0
		];

		var (indices, weights) = UnityTerrainConversion.PackLayerMaps(alphamaps, 2, 2, out var discarded);

		Assert.Multiple(() =>
		{
			Assert.That(indices[0], Is.EqualTo(1));
			Assert.That(indices[4], Is.EqualTo(2));
			Assert.That(indices[8], Is.EqualTo(1));
			Assert.That(weights[4], Is.EqualTo(255));
			Assert.That(discarded, Is.Zero);
		});
	}

	[Test]
	public void PackLayerMaps_FallsBackToTheAutomaticLayerWhereNothingIsPainted()
	{
		byte[] alphamaps = [0, 0];

		var (indices, weights) = UnityTerrainConversion.PackLayerMaps(alphamaps, 1, 2, out _);

		Assert.That(indices[0], Is.Zero);
		Assert.That(weights[0], Is.EqualTo(255));
	}

	[Test]
	public void TerrainEntity_SitsAtTheCentreOfTheUnityTerrain()
	{
		var terrain = new UnityTerrainExport
		{
			Position = [10.0f, 2.0f, -20.0f],
			Size = [256.0f, 80.0f, 128.0f]
		};

		Assert.That(UnityTerrainConversion.GetEntityPosition(terrain), Is.EqualTo(new Vector3(138.0f, 2.0f, 44.0f)));
	}

	[Test]
	public void TerrainComponent_DrawsItsNearestLevelAtTheHeightmapsSpacing()
	{
		var terrain = new UnityTerrainExport
		{
			Size = [256.0f, 80.0f, 256.0f],
			HeightmapResolution = 513
		};
		var component = new TerrainComponent();
		component.ApplyDefaultValues(null!, default);

		UnityTerrainConversion.ApplyTo(ref component, terrain);

		Assert.Multiple(() =>
		{
			Assert.That(component.WorldSizeMeters, Is.EqualTo(new Vector2(256.0f, 256.0f)));
			Assert.That(component.HeightScaleMeters, Is.EqualTo(80.0f));

			// 0.5 m between samples over a 64 m chunk.
			Assert.That(component.Lod0ResolutionInQuads, Is.EqualTo(128));
			Assert.That(component.RayTracingResolutionInQuads, Is.EqualTo(64));
		});
	}

	[Test]
	public void TreeInstance_IsInTheTerrainsLocalSpaceWithBothScales()
	{
		var tree = new UnityTreeExport
		{
			Position = [0.25f, 0.5f, 1.0f],
			Rotation = 0.0f,
			WidthScale = 0.8f,
			HeightScale = 1.6f
		};

		var instance = UnityTerrainConversion.CreateTreeInstance(tree, new Vector3(256.0f, 80.0f, 128.0f), 3);

		Assert.Multiple(() =>
		{
			Assert.That(instance.Position, Is.EqualTo(new Vector3(-64.0f, 40.0f, 64.0f)));
			Assert.That(instance.HeightScale, Is.EqualTo(1.6f));
			Assert.That(instance.WidthScale, Is.EqualTo(0.8f));
			Assert.That(instance.TypeIndex, Is.EqualTo(3));
		});
	}

	[Test]
	public void TreeRotation_TurnsAboutUpOnly()
	{
		var rotation = UnitySpace.ToTreeRotation(MathF.PI / 2.0f);
		var forward = Vector3.Transform(Vector3.UnitZ, rotation);

		Assert.Multiple(() =>
		{
			Assert.That(Vector3.Distance(Vector3.Transform(Vector3.UnitY, rotation), Vector3.UnitY), Is.LessThan(0.0001f));

			// Both engines are left-handed, so Unity's quarter turn about +Y takes +Z to +X here too.
			Assert.That(Vector3.Distance(forward, Vector3.UnitX), Is.LessThan(0.0001f));
		});
	}

	[Test]
	public void ColorTemperature_IsWarmBelowDaylightAndNormalisedToItsBrightestChannel()
	{
		var warm = UnitySpace.ColorTemperatureToLinear(3000.0f);
		var daylight = UnitySpace.ColorTemperatureToLinear(6500.0f);

		Assert.Multiple(() =>
		{
			Assert.That(warm.X, Is.EqualTo(1.0f));
			Assert.That(warm.Z, Is.LessThan(warm.Y));
			Assert.That(daylight.X, Is.EqualTo(1.0f).Within(0.02f));
			Assert.That(daylight.Z, Is.GreaterThan(0.85f));
		});
	}

	[Test]
	public void Load_RefusesAnotherFormatVersion()
	{
		var folder = Path.Combine(Path.GetTempPath(), "WolfEngineUnityExportTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			File.WriteAllText(Path.Combine(folder, UnitySceneExport.SceneFileName), "{ \"formatVersion\": 99 }");

			Assert.That(() => UnitySceneExport.Load(folder), Throws.TypeOf<InvalidDataException>());
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
