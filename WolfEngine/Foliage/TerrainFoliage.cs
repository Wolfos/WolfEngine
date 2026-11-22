using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine;

/// <summary>
/// The placed foliage of one terrain: trees, bushes and rocks stored as compact instances in a grid of cells, in the
/// terrain entity's local space. A terrain is centred on its entity, so that space's origin is the terrain's centre, not
/// its corner. Referenced from <see cref="TerrainComponent.Foliage"/>. Instances are not entities;
/// they are drawn by the GPU cull directly. See docs/foliage.md.
/// </summary>
[RuntimeAsset(AssetType.DataAsset, typeof(TerrainFoliage), typeof(IDataAssetRuntimeResolver))]
public sealed class TerrainFoliage : IDataAsset
{
	public const float DefaultCellSizeMeters = 64.0f;
	private List<TerrainFoliageCell>? _orderedCells;
	private int _orderedCellCount;

	/// <summary>The foliage types instances refer to by index.</summary>
	public List<AssetRef<FoliageType>> Types { get; set; } = [];

	/// <summary>Width and depth of a cell, in the terrain's local units.</summary>
	public float CellSizeMeters { get; set; } = DefaultCellSizeMeters;

	/// <summary>The populated cells. Use the cell editing methods to update this list.</summary>
	[HideFromEditor]
	public List<TerrainFoliageCell> Cells { get; set; } = [];

	[JsonIgnore]
	internal float ResolvedCellSizeMeters =>
		float.IsFinite(CellSizeMeters) && CellSizeMeters > 0.01f ? CellSizeMeters : DefaultCellSizeMeters;

	/// <summary>Changes whenever the instances change, so runtime copies know to rebuild. Not authored.</summary>
	[JsonIgnore]
	internal int Revision { get; private set; }

	[JsonIgnore]
	public int InstanceCount
	{
		get
		{
			var count = 0;
			foreach (var cell in Cells)
			{
				count += cell.InstanceCount;
			}

			return count;
		}
	}

	public void MarkChanged()
	{
		Revision++;
	}

	public (int X, int Z) GetCellCoordinates(float localX, float localZ)
	{
		var cellSize = ResolvedCellSizeMeters;
		return (GetGridCoordinate(localX, cellSize), GetGridCoordinate(localZ, cellSize));
	}

	private static int GetGridCoordinate(float position, float cellSize) =>
		(int)Math.Clamp(Math.Floor((double)position / cellSize), int.MinValue, int.MaxValue);

	public TerrainFoliageCell? GetCell(int x, int z)
	{
		var index = FindCellIndex(x, z);
		return index >= 0 ? Cells[index] : null;
	}

	internal IEnumerable<TerrainFoliageCell> GetCellsInRange(int minX, int minZ, int maxX, int maxZ)
	{
		var index = GetCellLowerBound(minX, minZ);
		while (index < Cells.Count)
		{
			var cell = Cells[index];
			if (cell.Z > maxZ) yield break;

			if (cell.X < minX)
			{
				index = GetCellLowerBound(minX, cell.Z);
				continue;
			}

			if (cell.X > maxX)
			{
				if (cell.Z == maxZ) yield break;

				index = GetCellLowerBound(minX, cell.Z + 1);
				continue;
			}

			yield return cell;
			index++;
		}
	}

	private int GetCellLowerBound(int x, int z)
	{
		var index = FindCellIndex(x, z);
		return index >= 0 ? index : ~index;
	}

	/// <summary>Replaces or removes one cell. Call <see cref="MarkChanged"/> after the batch of edits.</summary>
	public void ReplaceCell(int x, int z, IReadOnlyList<FoliageInstance> instances)
	{
		ArgumentNullException.ThrowIfNull(instances);
		foreach (var instance in instances)
		{
			ValidateTypeIndex(instance.TypeIndex);
		}

		RestoreCell(x, z, instances.Count == 0 ? null : TerrainFoliageCell.Create(x, z, instances));
	}

	internal void RestoreCell(int x, int z, TerrainFoliageCell? cell)
	{
		var index = FindCellIndex(x, z);
		if (index >= 0)
		{
			if (cell is null)
			{
				Cells.RemoveAt(index);
			}
			else
			{
				Cells[index] = cell;
			}
		}
		else if (cell is not null)
		{
			Cells.Insert(~index, cell);
		}

		_orderedCellCount = Cells.Count;
	}

	private int FindCellIndex(int x, int z)
	{
		EnsureCellOrder();
		var low = 0;
		var high = Cells.Count - 1;
		while (low <= high)
		{
			var middle = low + (high - low) / 2;
			var order = CompareCell(Cells[middle], x, z);
			if (order == 0) return middle;

			if (order < 0)
			{
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return ~low;
	}

	private void EnsureCellOrder()
	{
		if (ReferenceEquals(_orderedCells, Cells) && _orderedCellCount == Cells.Count) return;

		// Bulk replacement writes sorted cells. Handle older hand-authored lists on their first edit.
		for (var i = 1; i < Cells.Count; i++)
		{
			var current = Cells[i];
			if (CompareCell(Cells[i - 1], current.X, current.Z) > 0)
			{
				Cells.Sort((left, right) => CompareCell(left, right.X, right.Z));
				break;
			}
		}

		_orderedCells = Cells;
		_orderedCellCount = Cells.Count;
	}

	private static int CompareCell(TerrainFoliageCell cell, int x, int z)
	{
		var row = cell.Z.CompareTo(z);
		return row == 0 ? cell.X.CompareTo(x) : row;
	}

	private void ValidateTypeIndex(int typeIndex)
	{
		if ((uint)typeIndex >= (uint)Types.Count || (uint)typeIndex > FoliageInstancePacking.MaxTypeIndex)
		{
			throw new ArgumentOutOfRangeException(nameof(typeIndex), typeIndex, "A foliage instance names an invalid type index.");
		}
	}

	/// <summary>
	/// Replaces every instance, sorting them into cells of <see cref="CellSizeMeters"/>. Each cell's bounds enclose its
	/// instances' positions, which the packed positions are quantised across.
	/// </summary>
	public void SetInstances(IReadOnlyList<FoliageInstance> instances)
	{
		ArgumentNullException.ThrowIfNull(instances);

		var instancesByCell = new Dictionary<(int X, int Z), List<FoliageInstance>>();
		foreach (var instance in instances)
		{
			ValidateTypeIndex(instance.TypeIndex);
			var key = GetCellCoordinates(instance.Position.X, instance.Position.Z);
			if (instancesByCell.TryGetValue(key, out var cellInstances) == false)
			{
				cellInstances = [];
				instancesByCell.Add(key, cellInstances);
			}

			cellInstances.Add(instance);
		}

		var packedCells = instancesByCell.OrderBy(entry => entry.Key.Z).ThenBy(entry => entry.Key.X)
			.Select(entry => TerrainFoliageCell.Create(entry.Key.X, entry.Key.Z, entry.Value))
			.ToList();
		Cells.Clear();
		Cells.AddRange(packedCells);
		_orderedCells = Cells;
		_orderedCellCount = Cells.Count;
		MarkChanged();
	}
}

public sealed class TerrainFoliageCell
{
	/// <summary>Cell column: the cell spans <c>[X, X + 1)</c> cell sizes along local X.</summary>
	public int X { get; set; }

	/// <summary>Cell row: the cell spans <c>[Z, Z + 1)</c> cell sizes along local Z.</summary>
	public int Z { get; set; }

	/// <summary>Smallest instance position in the cell, which packed positions are quantised from.</summary>
	public Vector3 BoundsMin { get; set; }

	/// <summary>Largest instance position in the cell, which packed positions are quantised to.</summary>
	public Vector3 BoundsMax { get; set; }

	/// <summary>
	/// Instances in their packed form, <see cref="FoliageInstancePacking.SizeInBytes"/> each. See
	/// <see cref="FoliageInstancePacking"/>.
	/// </summary>
	public byte[] Instances { get; set; } = [];

	[JsonIgnore]
	public int InstanceCount => Instances.Length / FoliageInstancePacking.SizeInBytes;

	public FoliageInstance GetInstance(int index)
	{
		if ((uint)index >= (uint)InstanceCount)
		{
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		return FoliageInstancePacking.Unpack(
			Instances.AsSpan(index * FoliageInstancePacking.SizeInBytes, FoliageInstancePacking.SizeInBytes),
			BoundsMin,
			BoundsMax);
	}

	public static TerrainFoliageCell Create(int x, int z, IReadOnlyList<FoliageInstance> instances)
	{
		ArgumentNullException.ThrowIfNull(instances);

		var boundsMin = new Vector3(float.MaxValue);
		var boundsMax = new Vector3(float.MinValue);
		foreach (var instance in instances)
		{
			boundsMin = Vector3.Min(boundsMin, instance.Position);
			boundsMax = Vector3.Max(boundsMax, instance.Position);
		}

		if (instances.Count == 0)
		{
			boundsMin = Vector3.Zero;
			boundsMax = Vector3.Zero;
		}

		var packed = new byte[instances.Count * FoliageInstancePacking.SizeInBytes];
		for (var i = 0; i < instances.Count; i++)
		{
			FoliageInstancePacking.Pack(
				instances[i],
				boundsMin,
				boundsMax,
				packed.AsSpan(i * FoliageInstancePacking.SizeInBytes, FoliageInstancePacking.SizeInBytes));
		}

		return new TerrainFoliageCell
		{
			X = x,
			Z = z,
			BoundsMin = boundsMin,
			BoundsMax = boundsMax,
			Instances = packed
		};
	}
}
