using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;

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

	/// <summary>The foliage types instances refer to by index.</summary>
	public List<AssetRef<FoliageType>> Types { get; set; } = [];

	/// <summary>Width and depth of a cell, in the terrain's local units.</summary>
	public float CellSizeMeters { get; set; } = DefaultCellSizeMeters;

	/// <summary>The cells that hold any instances.</summary>
	public List<TerrainFoliageCell> Cells { get; set; } = [];

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

	/// <summary>
	/// Replaces every instance, sorting them into cells of <see cref="CellSizeMeters"/>. Each cell's bounds enclose its
	/// instances' positions, which the packed positions are quantised across.
	/// </summary>
	public void SetInstances(IReadOnlyList<FoliageInstance> instances)
	{
		ArgumentNullException.ThrowIfNull(instances);

		var cellSize = CellSizeMeters > 0.01f ? CellSizeMeters : DefaultCellSizeMeters;
		var instancesByCell = new Dictionary<(int X, int Z), List<FoliageInstance>>();
		foreach (var instance in instances)
		{
			if ((uint)instance.TypeIndex >= (uint)Types.Count)
			{
				throw new ArgumentOutOfRangeException(
					nameof(instances),
					instance.TypeIndex,
					"A foliage instance names a type the asset does not list.");
			}

			var key = ((int)MathF.Floor(instance.Position.X / cellSize), (int)MathF.Floor(instance.Position.Z / cellSize));
			if (instancesByCell.TryGetValue(key, out var cellInstances) == false)
			{
				cellInstances = [];
				instancesByCell.Add(key, cellInstances);
			}

			cellInstances.Add(instance);
		}

		Cells.Clear();
		foreach (var ((x, z), cellInstances) in instancesByCell.OrderBy(entry => entry.Key.Z).ThenBy(entry => entry.Key.X))
		{
			Cells.Add(TerrainFoliageCell.Create(x, z, cellInstances));
		}

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
