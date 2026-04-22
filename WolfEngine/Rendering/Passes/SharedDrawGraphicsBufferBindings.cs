#nullable enable

using System;

namespace WolfEngine.Rendering.Passes;

public readonly struct SharedDrawGraphicsBufferBindings
{
	public SharedDrawGraphicsBufferBindings(
		uint instanceRegisterIndex,
		uint materialRegisterIndex,
		uint drawArgsRegisterIndex,
		uint materialGenerationRegisterIndex,
		uint? terrainMaterialRegisterIndex = null,
		uint? terrainLayerRegisterIndex = null,
		uint? pointLightRegisterIndex = null,
		uint? clusterHeaderRegisterIndex = null,
		uint? clusterLightIndexRegisterIndex = null)
	{
		InstanceRegisterIndex = instanceRegisterIndex;
		MaterialRegisterIndex = materialRegisterIndex;
		DrawArgsRegisterIndex = drawArgsRegisterIndex;
		MaterialGenerationRegisterIndex = materialGenerationRegisterIndex;
		TerrainMaterialRegisterIndex = terrainMaterialRegisterIndex;
		TerrainLayerRegisterIndex = terrainLayerRegisterIndex;
		PointLightRegisterIndex = pointLightRegisterIndex;
		ClusterHeaderRegisterIndex = clusterHeaderRegisterIndex;
		ClusterLightIndexRegisterIndex = clusterLightIndexRegisterIndex;
	}

	public uint InstanceRegisterIndex { get; }

	public uint MaterialRegisterIndex { get; }

	public uint DrawArgsRegisterIndex { get; }

	public uint MaterialGenerationRegisterIndex { get; }

	public uint? TerrainMaterialRegisterIndex { get; }

	public uint? TerrainLayerRegisterIndex { get; }

	public uint? PointLightRegisterIndex { get; }

	public uint? ClusterHeaderRegisterIndex { get; }

	public uint? ClusterLightIndexRegisterIndex { get; }

	public static SharedDrawGraphicsBufferBindings FromGBufferReflection(ShaderReflectionLayout reflection)
	{
		ArgumentNullException.ThrowIfNull(reflection);
		return new SharedDrawGraphicsBufferBindings(
			reflection.GetResource("g_InstanceTable").RegisterIndex,
			reflection.GetResource("g_MaterialTable").RegisterIndex,
			reflection.GetResource("g_DrawArgsTable").RegisterIndex,
			reflection.GetResource("g_MaterialGenerations").RegisterIndex,
			reflection.TryGetResource("g_TerrainMaterialTable", out var terrain) ? terrain.RegisterIndex : null,
			reflection.TryGetResource("g_TerrainLayerTable", out var terrainLayer) ? terrainLayer.RegisterIndex : null);
	}

	public static SharedDrawGraphicsBufferBindings FromTransparentReflection(ShaderReflectionLayout reflection)
	{
		ArgumentNullException.ThrowIfNull(reflection);
		return new SharedDrawGraphicsBufferBindings(
			reflection.GetResource("g_InstanceTable").RegisterIndex,
			reflection.GetResource("g_MaterialTable").RegisterIndex,
			reflection.GetResource("g_DrawArgsTable").RegisterIndex,
			reflection.GetResource("g_MaterialGenerations").RegisterIndex,
			pointLightRegisterIndex: reflection.TryGetResource("g_PointLights", out var pointLights) ? pointLights.RegisterIndex : null,
			clusterHeaderRegisterIndex: reflection.TryGetResource("g_ClusterHeaders", out var clusterHeaders) ? clusterHeaders.RegisterIndex : null,
			clusterLightIndexRegisterIndex: reflection.TryGetResource("g_ClusterLightIndices", out var clusterLightIndices) ? clusterLightIndices.RegisterIndex : null);
	}

	public static SharedDrawGraphicsBufferBindings FromShadowReflection(ShaderReflectionLayout reflection)
	{
		ArgumentNullException.ThrowIfNull(reflection);
		return new SharedDrawGraphicsBufferBindings(
			reflection.GetResource("g_InstanceTable").RegisterIndex,
			reflection.GetResource("g_MaterialTable").RegisterIndex,
			reflection.GetResource("g_DrawArgsTable").RegisterIndex,
			reflection.GetResource("g_MaterialGenerations").RegisterIndex);
	}
}
