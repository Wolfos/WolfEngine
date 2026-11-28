using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class DetailFoliageTests
{
	[TestCase(60.0f, 0.5f)]
	[TestCase(60.0f, 0.7071f)]
	[TestCase(17.0f, 1.3f)]
	[TestCase(80.0f, 2.0f)]
	public void Window_CoversTheDistanceWhereverTheCameraIs(float distance, float spacing)
	{
		var type = CreateType(distance, spacing);
		var tileSize = type.TileSize;

		foreach (var offset in new[] { 0.0f, 0.01f, 0.5f, 0.99f })
		{
			var camera = new Vector3((offset + 3.0f) * tileSize, 0.0f, (-offset - 7.0f) * tileSize);
			var (originX, originZ) = TerrainDetailRuntime.GetWindowOrigin(type, camera);
			var minX = originX * tileSize;
			var maxX = (originX + type.GridDimension) * tileSize;
			var minZ = originZ * tileSize;
			var maxZ = (originZ + type.GridDimension) * tileSize;

			Assert.Multiple(() =>
			{
				Assert.That(camera.X - minX, Is.GreaterThanOrEqualTo(distance - 1e-3f));
				Assert.That(maxX - camera.X, Is.GreaterThanOrEqualTo(distance - 1e-3f));
				Assert.That(camera.Z - minZ, Is.GreaterThanOrEqualTo(distance - 1e-3f));
				Assert.That(maxZ - camera.Z, Is.GreaterThanOrEqualTo(distance - 1e-3f));
			});
		}
	}

	[Test]
	public void TileSlots_AreUniqueInTheWindow_AndKeptWhileTheWindowMoves()
	{
		var type = CreateType(20.0f, 0.5f);
		var dimension = type.GridDimension;
		var slots = new Dictionary<(int X, int Z), int>();
		var used = new HashSet<int>();
		for (var z = -4; z < dimension - 4; z++)
		{
			for (var x = -2; x < dimension - 2; x++)
			{
				var slot = TerrainDetailRuntime.GetTileSlot(type, x, z);
				Assert.That(used.Add(slot), Is.True, $"tile ({x}, {z}) shares slot {slot}");
				slots[(x, z)] = slot;
			}
		}

		// One tile on, the window drops a column and gains one; every tile still in it keeps its slot.
		for (var z = -4; z < dimension - 4; z++)
		{
			for (var x = -1; x < dimension - 1; x++)
			{
				var slot = TerrainDetailRuntime.GetTileSlot(type, x, z);
				if (slots.TryGetValue((x, z), out var previous))
				{
					Assert.That(slot, Is.EqualTo(previous));
				}
				else
				{
					Assert.That(slot, Is.EqualTo(slots[(x - dimension, z)]), "a new tile takes the slot of the one that left");
				}
			}
		}
	}

	private static TerrainDetailTypeRuntime CreateType(float distance, float spacing)
	{
		var tileSize = spacing * TerrainDetailRuntime.TileCandidatesPerAxis;

		// Only the grid is under test, so no foliage type is resolved.
		return new TerrainDetailTypeRuntime(
			null!,
			0,
			1.0f / (spacing * spacing),
			spacing,
			distance,
			0.0f,
			TerrainDetailRuntime.GetGridDimension(distance, tileSize),
			0,
			0u);
	}
}
