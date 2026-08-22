namespace WolfEngine.Rendering.Passes;

public readonly struct SharedDrawGraphicsBufferBindings
{
	public SharedDrawGraphicsBufferBindings(
		uint cameraRegisterIndex,
		uint instanceRegisterIndex,
		uint materialRegisterIndex,
		uint drawArgsRegisterIndex,
		uint materialGenerationRegisterIndex,
		uint? terrainMaterialRegisterIndex = null,
		uint? terrainLayerRegisterIndex = null,
		uint? pointLightRegisterIndex = null,
		uint? clusterHeaderRegisterIndex = null,
		uint? clusterLightIndexRegisterIndex = null,
		uint? ddgiDebugRegisterIndex = null,
		uint? transparentEnvironmentRegisterIndex = null,
		uint? transparentLightingRegisterIndex = null)
	{
		CameraRegisterIndex = cameraRegisterIndex;
		InstanceRegisterIndex = instanceRegisterIndex;
		MaterialRegisterIndex = materialRegisterIndex;
		DrawArgsRegisterIndex = drawArgsRegisterIndex;
		MaterialGenerationRegisterIndex = materialGenerationRegisterIndex;
		TerrainMaterialRegisterIndex = terrainMaterialRegisterIndex;
		TerrainLayerRegisterIndex = terrainLayerRegisterIndex;
		PointLightRegisterIndex = pointLightRegisterIndex;
		ClusterHeaderRegisterIndex = clusterHeaderRegisterIndex;
		ClusterLightIndexRegisterIndex = clusterLightIndexRegisterIndex;
		DdgiDebugRegisterIndex = ddgiDebugRegisterIndex;
		TransparentEnvironmentRegisterIndex = transparentEnvironmentRegisterIndex;
		TransparentLightingRegisterIndex = transparentLightingRegisterIndex;
	}

	public uint CameraRegisterIndex { get; }

	public uint InstanceRegisterIndex { get; }

	public uint MaterialRegisterIndex { get; }

	public uint DrawArgsRegisterIndex { get; }

	public uint MaterialGenerationRegisterIndex { get; }

	public uint? TerrainMaterialRegisterIndex { get; }

	public uint? TerrainLayerRegisterIndex { get; }

	public uint? PointLightRegisterIndex { get; }

	public uint? ClusterHeaderRegisterIndex { get; }

	public uint? ClusterLightIndexRegisterIndex { get; }

	public uint? DdgiDebugRegisterIndex { get; }

	public uint? TransparentEnvironmentRegisterIndex { get; }

	public uint? TransparentLightingRegisterIndex { get; }

	public SharedDrawPerDrawBindings ToPerDrawBindings() => new(
		InstanceRegisterIndex,
		MaterialRegisterIndex,
		DrawArgsRegisterIndex,
		MaterialGenerationRegisterIndex);

	public static SharedDrawGraphicsBufferBindings FromGBufferReflection(ShaderReflectionLayout reflection)
	{
		ArgumentNullException.ThrowIfNull(reflection);
		return new SharedDrawGraphicsBufferBindings(
			reflection.GetConstantBuffer("CameraParams").RegisterIndex,
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
			reflection.GetConstantBuffer("CameraParams").RegisterIndex,
			reflection.GetResource("g_InstanceTable").RegisterIndex,
			reflection.GetResource("g_MaterialTable").RegisterIndex,
			reflection.GetResource("g_DrawArgsTable").RegisterIndex,
			reflection.GetResource("g_MaterialGenerations").RegisterIndex,
			pointLightRegisterIndex: reflection.TryGetResource("g_PointLights", out var pointLights) ? pointLights.RegisterIndex : null,
			clusterHeaderRegisterIndex: reflection.TryGetResource("g_ClusterHeaders", out var clusterHeaders) ? clusterHeaders.RegisterIndex : null,
			clusterLightIndexRegisterIndex: reflection.TryGetResource("g_ClusterLightIndices", out var clusterLightIndices) ? clusterLightIndices.RegisterIndex : null,
			ddgiDebugRegisterIndex: reflection.TryGetConstantBuffer("DdgiDebugParams", out var ddgiDebug) ? ddgiDebug.RegisterIndex : null,
			transparentEnvironmentRegisterIndex: reflection.TryGetConstantBuffer("TransparentEnvironmentParams", out var environment) ? environment.RegisterIndex : null,
			transparentLightingRegisterIndex: reflection.TryGetConstantBuffer("LightingParams", out var lighting) ? lighting.RegisterIndex : null);
	}

	public static SharedDrawGraphicsBufferBindings FromShadowReflection(ShaderReflectionLayout reflection)
	{
		ArgumentNullException.ThrowIfNull(reflection);
		return new SharedDrawGraphicsBufferBindings(
			reflection.GetConstantBuffer("CameraParams").RegisterIndex,
			reflection.GetResource("g_InstanceTable").RegisterIndex,
			reflection.GetResource("g_MaterialTable").RegisterIndex,
			reflection.GetResource("g_DrawArgsTable").RegisterIndex,
			reflection.GetResource("g_MaterialGenerations").RegisterIndex,
			reflection.TryGetResource("g_TerrainMaterialTable", out var terrain) ? terrain.RegisterIndex : null,
			reflection.TryGetResource("g_TerrainLayerTable", out var terrainLayer) ? terrainLayer.RegisterIndex : null);
	}
}
