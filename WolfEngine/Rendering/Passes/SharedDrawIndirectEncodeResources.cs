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
		IGfxBuffer? ddgiDebugBuffer,
		IGfxBuffer? instanceBuffer,
		IGfxBuffer? materialBuffer,
		IGfxBuffer? terrainMaterialBuffer,
		IGfxBuffer? terrainLayerBuffer,
		IGfxBuffer? drawArgsBuffer,
		ulong drawArgsBaseOffsetBytes,
		IGfxBuffer? materialGenerationBuffer)
	{
		CameraBuffer = cameraBuffer;
		ShadowCameraBuffer = shadowCameraBuffer;
		TransparentEnvironmentBuffer = transparentEnvironmentBuffer;
		TransparentLightingBuffer = transparentLightingBuffer;
		DdgiDebugBuffer = ddgiDebugBuffer;
		InstanceBuffer = instanceBuffer;
		MaterialBuffer = materialBuffer;
		TerrainMaterialBuffer = terrainMaterialBuffer;
		TerrainLayerBuffer = terrainLayerBuffer;
		DrawArgsBuffer = drawArgsBuffer;
		DrawArgsBaseOffsetBytes = drawArgsBaseOffsetBytes;
		MaterialGenerationBuffer = materialGenerationBuffer;
	}

	public IGfxBuffer? CameraBuffer { get; }
	public IGfxBuffer? ShadowCameraBuffer { get; }
	public IGfxBuffer? TransparentEnvironmentBuffer { get; }
	public IGfxBuffer? TransparentLightingBuffer { get; }
	public IGfxBuffer? DdgiDebugBuffer { get; }
	public IGfxBuffer? InstanceBuffer { get; }
	public IGfxBuffer? MaterialBuffer { get; }
	public IGfxBuffer? TerrainMaterialBuffer { get; }
	public IGfxBuffer? TerrainLayerBuffer { get; }
	public IGfxBuffer? DrawArgsBuffer { get; }
	public ulong DrawArgsBaseOffsetBytes { get; }
	public IGfxBuffer? MaterialGenerationBuffer { get; }

	public static SharedDrawIndirectEncodeResources FromGpuDrawResources(
		GpuDrawResources resources,
		IGfxBuffer? cameraBuffer,
		IGfxBuffer? drawArgsBuffer = null,
		ulong drawArgsBaseOffsetBytes = 0)
	{
		ArgumentNullException.ThrowIfNull(resources);
		return new SharedDrawIndirectEncodeResources(
			cameraBuffer,
			resources.ShadowCameraBuffer,
			resources.TransparentEnvironmentBuffer,
			resources.TransparentLightingBuffer,
			resources.DdgiDebugBuffer,
			resources.InstanceBuffer,
			resources.MaterialBuffer,
			resources.TerrainMaterialBuffer,
			resources.TerrainLayerBuffer,
			drawArgsBuffer ?? resources.DrawArgsBuffer,
			drawArgsBaseOffsetBytes,
			resources.MaterialGenerationBuffer);
	}
}
