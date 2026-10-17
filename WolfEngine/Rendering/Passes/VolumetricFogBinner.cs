using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Passes;

internal readonly record struct VolumetricFogBinningResult(
	GpuFogVolumeData[] Volumes,
	GpuFogCellHeader[] CellHeaders,
	uint[] VolumeIndices,
	Int3 CellGrid);

internal static class VolumetricFogBinner
{
	public const int MacroCellSize = 4;

	public static VolumetricFogBinningResult Build(
		SceneDrawData sceneData,
		Int3 froxelGrid,
		float maxDistance)
	{
		var cellGrid = new Int3(
			DivideRoundUp(froxelGrid.X, MacroCellSize),
			DivideRoundUp(froxelGrid.Y, MacroCellSize),
			DivideRoundUp(froxelGrid.Z, MacroCellSize));
		var cells = new List<uint>[cellGrid.X * cellGrid.Y * cellGrid.Z];
		var volumes = new List<GpuFogVolumeData>(sceneData.FogVolumes.Count);
		var near = MathF.Max(sceneData.NearPlane, 0.001f);
		var far = MathF.Max(maxDistance, near + 0.001f);

		foreach (var packet in sceneData.FogVolumes)
		{
			var volume = packet.Volume;
			var localToWorld = Matrix4x4.CreateScale(volume.Size) * packet.Transform;
			if (Matrix4x4.Invert(localToWorld, out var worldToLocal) == false)
			{
				continue;
			}

			if (TryProjectBounds(sceneData, localToWorld, froxelGrid, near, far, out var min, out var max) == false)
			{
				continue;
			}

			var axisX = Vector3.TransformNormal(Vector3.UnitX, localToWorld).Length();
			var axisY = Vector3.TransformNormal(Vector3.UnitY, localToWorld).Length();
			var axisZ = Vector3.TransformNormal(Vector3.UnitZ, localToWorld).Length();
			var minimumSize = MathF.Max(MathF.Min(axisX, MathF.Min(axisY, axisZ)), 1e-4f);
			var normalizedBlend = Math.Clamp(volume.BlendDistance / minimumSize, 0.0f, 0.5f);
			var volumeIndex = (uint)volumes.Count;
			volumes.Add(new GpuFogVolumeData(
				worldToLocal,
				new Vector4(Vector3.Max(volume.Albedo, Vector3.Zero), MathF.Max(volume.Extinction, 0.0f)),
				new Vector4(Math.Clamp(volume.Anisotropy, -0.95f, 0.95f), normalizedBlend, (float)volume.Shape, 0.0f)));

			var minCell = new Int3(min.X / MacroCellSize, min.Y / MacroCellSize, min.Z / MacroCellSize);
			var maxCell = new Int3(max.X / MacroCellSize, max.Y / MacroCellSize, max.Z / MacroCellSize);
			for (var z = minCell.Z; z <= maxCell.Z; z++)
			for (var y = minCell.Y; y <= maxCell.Y; y++)
			for (var x = minCell.X; x <= maxCell.X; x++)
			{
				var cellIndex = x + cellGrid.X * (y + cellGrid.Y * z);
				(cells[cellIndex] ??= []).Add(volumeIndex);
			}
		}

		var headers = new GpuFogCellHeader[cells.Length];
		var indices = new List<uint>();
		for (var i = 0; i < cells.Length; i++)
		{
			var cell = cells[i];
			headers[i] = new GpuFogCellHeader((uint)indices.Count, (uint)(cell?.Count ?? 0));
			if (cell is not null) indices.AddRange(cell);
		}

		return new VolumetricFogBinningResult(volumes.ToArray(), headers, indices.ToArray(), cellGrid);
	}

	private static bool TryProjectBounds(
		SceneDrawData sceneData,
		Matrix4x4 localToWorld,
		Int3 grid,
		float near,
		float far,
		out Int3 min,
		out Int3 max)
	{
		var minNdc = new Vector2(float.PositiveInfinity);
		var maxNdc = new Vector2(float.NegativeInfinity);
		var minDepth = float.PositiveInfinity;
		var maxDepth = float.NegativeInfinity;
		var crossesNear = false;
		for (var z = 0; z < 2; z++)
		for (var y = 0; y < 2; y++)
		for (var x = 0; x < 2; x++)
		{
			var local = new Vector3(x - 0.5f, y - 0.5f, z - 0.5f);
			var absolute = Vector3.Transform(local, localToWorld);
			var relative = absolute - sceneData.CameraOrigin;
			var view = Vector3.Transform(relative, sceneData.ViewMatrix);
			minDepth = MathF.Min(minDepth, view.Z);
			maxDepth = MathF.Max(maxDepth, view.Z);
			if (view.Z <= near)
			{
				crossesNear = true;
				continue;
			}
			var clip = Vector4.Transform(new Vector4(relative, 1.0f), sceneData.UnjitteredViewProjection);
			if (MathF.Abs(clip.W) <= 1e-6f) continue;
			var ndc = new Vector2(clip.X, clip.Y) / clip.W;
			minNdc = Vector2.Min(minNdc, ndc);
			maxNdc = Vector2.Max(maxNdc, ndc);
		}

		if (maxDepth < near || minDepth > far)
		{
			min = max = default;
			return false;
		}

		if (crossesNear || !float.IsFinite(minNdc.X))
		{
			minNdc = new Vector2(-1.0f);
			maxNdc = new Vector2(1.0f);
		}
		if (maxNdc.X < -1.0f || minNdc.X > 1.0f || maxNdc.Y < -1.0f || minNdc.Y > 1.0f)
		{
			min = max = default;
			return false;
		}

		var minX = Math.Clamp((int)MathF.Floor((minNdc.X * 0.5f + 0.5f) * grid.X), 0, grid.X - 1);
		var maxX = Math.Clamp((int)MathF.Floor((maxNdc.X * 0.5f + 0.5f) * grid.X), 0, grid.X - 1);
		// Froxel Y follows screen UV (origin top-left), so the top NDC edge maps to the lowest row.
		var minY = Math.Clamp((int)MathF.Floor((0.5f - maxNdc.Y * 0.5f) * grid.Y), 0, grid.Y - 1);
		var maxY = Math.Clamp((int)MathF.Floor((0.5f - minNdc.Y * 0.5f) * grid.Y), 0, grid.Y - 1);
		var minZ = DepthToSlice(MathF.Max(minDepth, near), near, far, grid.Z);
		var maxZ = DepthToSlice(MathF.Min(maxDepth, far), near, far, grid.Z);
		min = new Int3(Math.Min(minX, maxX), Math.Min(minY, maxY), Math.Min(minZ, maxZ));
		max = new Int3(Math.Max(minX, maxX), Math.Max(minY, maxY), Math.Max(minZ, maxZ));
		return true;
	}

	public static int DepthToSlice(float depth, float near, float far, int sliceCount)
	{
		var normalized = MathF.Log(Math.Clamp(depth, near, far) / near) / MathF.Log(far / near);
		return Math.Clamp((int)MathF.Floor(normalized * sliceCount), 0, sliceCount - 1);
	}

	private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
}
