#nullable enable

using System;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct SharedDrawIndirectEncodeResources
{
	public SharedDrawIndirectEncodeResources(
		IGfxBuffer? cameraBuffer,
		IGfxBuffer? shadowCameraBuffer,
		IGfxBuffer? transparentEnvironmentBuffer,
		IGfxBuffer? transparentLightingBuffer,
		IGfxBuffer? instanceBuffer,
		IGfxBuffer? materialBuffer,
		IGfxBuffer? terrainMaterialBuffer,
		IGfxBuffer? terrainLayerBuffer,
		IGfxBuffer? drawArgsBuffer,
		IGfxBuffer? materialGenerationBuffer)
	{
		CameraBuffer = cameraBuffer;
		ShadowCameraBuffer = shadowCameraBuffer;
		TransparentEnvironmentBuffer = transparentEnvironmentBuffer;
		TransparentLightingBuffer = transparentLightingBuffer;
		InstanceBuffer = instanceBuffer;
		MaterialBuffer = materialBuffer;
		TerrainMaterialBuffer = terrainMaterialBuffer;
		TerrainLayerBuffer = terrainLayerBuffer;
		DrawArgsBuffer = drawArgsBuffer;
		MaterialGenerationBuffer = materialGenerationBuffer;
	}

	public IGfxBuffer? CameraBuffer { get; }
	public IGfxBuffer? ShadowCameraBuffer { get; }
	public IGfxBuffer? TransparentEnvironmentBuffer { get; }
	public IGfxBuffer? TransparentLightingBuffer { get; }
	public IGfxBuffer? InstanceBuffer { get; }
	public IGfxBuffer? MaterialBuffer { get; }
	public IGfxBuffer? TerrainMaterialBuffer { get; }
	public IGfxBuffer? TerrainLayerBuffer { get; }
	public IGfxBuffer? DrawArgsBuffer { get; }
	public IGfxBuffer? MaterialGenerationBuffer { get; }

	public static SharedDrawIndirectEncodeResources FromGpuDrawResources(
		GpuDrawResources resources,
		IGfxBuffer? cameraBuffer)
	{
		ArgumentNullException.ThrowIfNull(resources);
		return new SharedDrawIndirectEncodeResources(
			cameraBuffer,
			resources.ShadowCameraBuffer,
			resources.TransparentEnvironmentBuffer,
			resources.TransparentLightingBuffer,
			resources.InstanceBuffer,
			resources.MaterialBuffer,
			resources.TerrainMaterialBuffer,
			resources.TerrainLayerBuffer,
			resources.DrawArgsBuffer,
			resources.MaterialGenerationBuffer);
	}
}

