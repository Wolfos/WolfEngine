#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

internal static class ClusteredLightingShared
{
	public const int TileSizePixels = 64;
	public const int ZSliceCount = 24;
	public const int MaxDirectionalLights = 4;
	public const int MaxPointLights = 1024;
	public const int DefaultMaxLightsPerCluster = 64;

	public static Int3 ComputeGrid(Int2 framebufferSize)
	{
		var clusterCountX = Math.Max(1, (framebufferSize.X + TileSizePixels - 1) / TileSizePixels);
		var clusterCountY = Math.Max(1, (framebufferSize.Y + TileSizePixels - 1) / TileSizePixels);
		return new Int3(clusterCountX, clusterCountY, ZSliceCount);
	}

	public static int ComputeClusterCount(Int2 framebufferSize)
	{
		var grid = ComputeGrid(framebufferSize);
		return checked(grid.X * grid.Y * grid.Z);
	}

	public static int ComputeIndexCapacity(Int2 framebufferSize)
	{
		return checked(ComputeClusterCount(framebufferSize) * DefaultMaxLightsPerCluster);
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct DirectionalLightGpuData
{
	public Vector4 ColorIntensity;
	public Vector4 DirectionAndType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointLightGpuData
{
	public Vector4 ColorIntensity;
	public Vector4 WorldPositionRange;
	public Vector4 ViewPositionRange;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClusterAabbGpuData
{
	public Vector4 MinPoint;
	public Vector4 MaxPoint;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClusterHeaderGpuData
{
	public uint Offset;
	public uint Count;
}

public readonly record struct ClusteredLightingFrameLayout(
	Int3 Grid,
	int ClusterCount,
	int LightIndexCapacity);

public readonly record struct Int3(int X, int Y, int Z);
