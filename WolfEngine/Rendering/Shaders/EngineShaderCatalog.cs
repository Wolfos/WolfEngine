#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

public readonly record struct ShaderProgramId
{
	public ShaderProgramId(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.StartsWith("engine://shaders/", StringComparison.Ordinal) == false)
		{
			throw new ArgumentException("Engine shader IDs must use the 'engine://shaders/' namespace.", nameof(value));
		}

		Value = value;
	}

	public string Value { get; }

	public override string ToString() => Value;
}

public sealed record EngineShaderProgramDescriptor(ShaderProgramId Id, string RelativeSourcePath);

public static class EngineShaderPrograms
{
	public static readonly ShaderProgramId GBuffer = Id("gbuffer");
	public static readonly ShaderProgramId DeferredLighting = Id("deferred-lighting");
	public static readonly ShaderProgramId ClusteredLighting = Id("clustered-lighting");
	public static readonly ShaderProgramId ImGui = Id("imgui");
	public static readonly ShaderProgramId ProceduralSkybox = Id("procedural-skybox");
	public static readonly ShaderProgramId IblBrdfLut = Id("ibl-brdf-lut");
	public static readonly ShaderProgramId IblIrradiance = Id("ibl-irradiance");
	public static readonly ShaderProgramId IblPrefilter = Id("ibl-prefilter");
	public static readonly ShaderProgramId GpuDrawCull = Id("gpu-draw-cull");
	public static readonly ShaderProgramId GpuDrawCompact = Id("gpu-draw-compact");
	public static readonly ShaderProgramId TransparentForward = Id("transparent-forward");
	public static readonly ShaderProgramId ShadowMap = Id("shadow-map");
	public static readonly ShaderProgramId AmbientOcclusionVbao = Id("ao-vbao");
	public static readonly ShaderProgramId AmbientOcclusionRayTraced = Id("ao-rtao");
	public static readonly ShaderProgramId ReflectionsScreenSpace = Id("reflections-ssr");
	public static readonly ShaderProgramId ReflectionsRayTraced = Id("reflections-rt");
	public static readonly ShaderProgramId ReflectionsUpsample = Id("reflections-upsample");
	public static readonly ShaderProgramId DdgiClassify = Id("ddgi-classify");
	public static readonly ShaderProgramId DdgiTrace = Id("ddgi-trace");
	public static readonly ShaderProgramId DdgiRelocate = Id("ddgi-relocate");
	public static readonly ShaderProgramId DdgiIrradianceIntegrate = Id("ddgi-irradiance-integrate");
	public static readonly ShaderProgramId DdgiIntegrate = Id("ddgi-integrate");
	public static readonly ShaderProgramId AmbientOcclusionBlur = Id("ao-blur");
	public static readonly ShaderProgramId AmbientOcclusionUpsample = Id("ao-upsample");
	public static readonly ShaderProgramId TaaResolve = Id("taa-resolve");
	public static readonly ShaderProgramId TaaHistoryStore = Id("taa-history-store");
	public static readonly ShaderProgramId Fsr3Rcas = Id("fsr3-rcas");
	public static readonly ShaderProgramId Fsr3PrepareInputs = Id("fsr3-prepare-inputs");
	public static readonly ShaderProgramId Fsr3LumaPyramid = Id("fsr3-luma-pyramid");
	public static readonly ShaderProgramId Fsr3ShadingChangePyramid = Id("fsr3-shading-change-pyramid");
	public static readonly ShaderProgramId Fsr3ShadingChange = Id("fsr3-shading-change");
	public static readonly ShaderProgramId Fsr3PrepareReactivity = Id("fsr3-prepare-reactivity");
	public static readonly ShaderProgramId Fsr3DebugView = Id("fsr3-debug-view");
	public static readonly ShaderProgramId Fsr3LumaInstability = Id("fsr3-luma-instability");
	public static readonly ShaderProgramId Fsr3Accumulate = Id("fsr3-accumulate");
	public static readonly ShaderProgramId Fsr3Clear = Id("fsr3-clear");
	public static readonly ShaderProgramId Tonemapping = Id("tonemapping");
	public static readonly ShaderProgramId Bloom = Id("bloom");
	public static readonly ShaderProgramId ColorPyramid = Id("color-pyramid");
	public static readonly ShaderProgramId CasSharpen = Id("cas-sharpen");
	public static readonly ShaderProgramId CopyToFinal = Id("copy-to-final");
	public static readonly ShaderProgramId MotionVectorDebug = Id("motion-vector-debug");
	public static readonly ShaderProgramId Bc1Compress = Id("bc1-compress");
	public static readonly ShaderProgramId Bc4Compress = Id("bc4-compress");
	public static readonly ShaderProgramId Bc3Stitch = Id("bc3-stitch");
	public static readonly ShaderProgramId Bc5Stitch = Id("bc5-stitch");
	public static readonly ShaderProgramId TerrainSharedGBuffer = Id("terrain-shared-gbuffer");
	public static readonly ShaderProgramId DebugPrimitiveForward = Id("debug-primitive-forward");
	public static readonly ShaderProgramId DebugPrimitiveGBuffer = Id("debug-primitive-gbuffer");
	public static readonly ShaderProgramId GpuDrawInstanceUpdate = Id("gpu-draw-instance-update");
	public static readonly ShaderProgramId GpuDrawMaterialUpdate = Id("gpu-draw-material-update");
	public static readonly ShaderProgramId GpuDrawMeshUpdate = Id("gpu-draw-mesh-update");
	public static readonly ShaderProgramId GpuDrawTerrainLayerUpdate = Id("gpu-draw-terrain-layer-update");
	public static readonly ShaderProgramId GpuDrawTerrainMaterialUpdate = Id("gpu-draw-terrain-material-update");
	public static readonly ShaderProgramId TerrainRayTracingVertexUpdate = Id("terrain-rt-vertex-update");
	public static readonly ShaderProgramId Skinning = Id("skinning");
	public static readonly ShaderProgramId TerrainAuthoringBrushes = Id("terrain-authoring-brushes");
	public static readonly ShaderProgramId ScreenSpaceDecal = Id("screen-space-decal");
	public static readonly ShaderProgramId GBufferDecalSeed = Id("gbuffer-decal-seed");

	private static ShaderProgramId Id(string name) => new($"engine://shaders/{name}");
}

public sealed class EngineShaderCatalog
{
	public const int Version = 1;

	private readonly Dictionary<ShaderProgramId, EngineShaderProgramDescriptor> _byId;
	private readonly Dictionary<string, EngineShaderProgramDescriptor> _bySourcePath;

	public EngineShaderCatalog()
	{
		var descriptors = new[]
		{
			D(EngineShaderPrograms.GBuffer, "Geometry/gbuffer.slang"),
			D(EngineShaderPrograms.DeferredLighting, "Lighting/deferred_lighting.compute.slang"),
			D(EngineShaderPrograms.ClusteredLighting, "Lighting/clustered_lighting.compute.slang"),
			D(EngineShaderPrograms.ImGui, "Ui/imgui.slang"),
			D(EngineShaderPrograms.ProceduralSkybox, "Ibl/procedural_skybox.compute.slang"),
			D(EngineShaderPrograms.IblBrdfLut, "Ibl/ibl_brdf_lut.compute.slang"),
			D(EngineShaderPrograms.IblIrradiance, "Ibl/ibl_irradiance.compute.slang"),
			D(EngineShaderPrograms.IblPrefilter, "Ibl/ibl_prefilter.compute.slang"),
			D(EngineShaderPrograms.GpuDrawCull, "GpuDraw/gpu_draw_cull.compute.slang"),
			D(EngineShaderPrograms.GpuDrawCompact, "GpuDraw/gpu_draw_compact.compute.slang"),
			D(EngineShaderPrograms.TransparentForward, "Geometry/transparent_forward.slang"),
			D(EngineShaderPrograms.ShadowMap, "Geometry/shadow_map.slang"),
			D(EngineShaderPrograms.AmbientOcclusionVbao, "AmbientOcclusion/ao_vbao.compute.slang"),
			D(EngineShaderPrograms.AmbientOcclusionRayTraced, "AmbientOcclusion/ao_rtao.compute.slang"),
			D(EngineShaderPrograms.ReflectionsScreenSpace, "Reflections/reflections_ssr.compute.slang"),
			D(EngineShaderPrograms.ReflectionsRayTraced, "Reflections/reflections_rt.compute.slang"),
			D(EngineShaderPrograms.ReflectionsUpsample, "Reflections/reflections_upsample.compute.slang"),
			D(EngineShaderPrograms.DdgiClassify, "Ddgi/ddgi_classify.compute.slang"),
			D(EngineShaderPrograms.DdgiTrace, "Ddgi/ddgi_trace.compute.slang"),
			D(EngineShaderPrograms.DdgiRelocate, "Ddgi/ddgi_relocate.compute.slang"),
			D(EngineShaderPrograms.DdgiIrradianceIntegrate, "Ddgi/ddgi_irradiance_integrate.compute.slang"),
			D(EngineShaderPrograms.DdgiIntegrate, "Ddgi/ddgi_integrate.compute.slang"),
			D(EngineShaderPrograms.AmbientOcclusionBlur, "AmbientOcclusion/ao_blur.compute.slang"),
			D(EngineShaderPrograms.AmbientOcclusionUpsample, "AmbientOcclusion/ao_upsample.compute.slang"),
			D(EngineShaderPrograms.TaaResolve, "Taa/taa_resolve.compute.slang"),
			D(EngineShaderPrograms.TaaHistoryStore, "Taa/taa_history_store.compute.slang"),
			D(EngineShaderPrograms.Fsr3Rcas, "ThirdParty/Fsr3/fsr3_rcas.compute.slang"),
			D(EngineShaderPrograms.Fsr3PrepareInputs, "ThirdParty/Fsr3/fsr3_prepare_inputs.compute.slang"),
			D(EngineShaderPrograms.Fsr3LumaPyramid, "ThirdParty/Fsr3/fsr3_luma_pyramid.compute.slang"),
			D(EngineShaderPrograms.Fsr3ShadingChangePyramid, "ThirdParty/Fsr3/fsr3_shading_change_pyramid.compute.slang"),
			D(EngineShaderPrograms.Fsr3ShadingChange, "ThirdParty/Fsr3/fsr3_shading_change.compute.slang"),
			D(EngineShaderPrograms.Fsr3PrepareReactivity, "ThirdParty/Fsr3/fsr3_prepare_reactivity.compute.slang"),
			D(EngineShaderPrograms.Fsr3DebugView, "ThirdParty/Fsr3/fsr3_debug_view.compute.slang"),
			D(EngineShaderPrograms.Fsr3LumaInstability, "ThirdParty/Fsr3/fsr3_luma_instability.compute.slang"),
			D(EngineShaderPrograms.Fsr3Accumulate, "ThirdParty/Fsr3/fsr3_accumulate.compute.slang"),
			D(EngineShaderPrograms.Fsr3Clear, "ThirdParty/Fsr3/fsr3_clear.compute.slang"),
			D(EngineShaderPrograms.Tonemapping, "PostProcess/tonemapping.compute.slang"),
			D(EngineShaderPrograms.Bloom, "PostProcess/bloom.compute.slang"),
			D(EngineShaderPrograms.ColorPyramid, "PostProcess/color_pyramid.compute.slang"),
			D(EngineShaderPrograms.CasSharpen, "ThirdParty/FfxCas/cas_sharpen.compute.slang"),
			D(EngineShaderPrograms.CopyToFinal, "PostProcess/copy_to_final.compute.slang"),
			D(EngineShaderPrograms.MotionVectorDebug, "PostProcess/motion_vector_debug.compute.slang"),
			D(EngineShaderPrograms.Bc1Compress, "ThirdParty/Betsy/bc1_compress.compute.slang"),
			D(EngineShaderPrograms.Bc4Compress, "ThirdParty/Betsy/bc4_compress.compute.slang"),
			D(EngineShaderPrograms.Bc3Stitch, "ThirdParty/Betsy/bc3_stitch.compute.slang"),
			D(EngineShaderPrograms.Bc5Stitch, "ThirdParty/Betsy/bc5_stitch.compute.slang"),
			D(EngineShaderPrograms.TerrainSharedGBuffer, "Terrain/terrain_shared_gbuffer.slang"),
			D(EngineShaderPrograms.DebugPrimitiveForward, "Geometry/debug_primitive_forward.slang"),
			D(EngineShaderPrograms.DebugPrimitiveGBuffer, "Geometry/debug_primitive_gbuffer.slang"),
			D(EngineShaderPrograms.GpuDrawInstanceUpdate, "GpuDraw/gpu_draw_instance_update.compute.slang"),
			D(EngineShaderPrograms.GpuDrawMaterialUpdate, "GpuDraw/gpu_draw_material_update.compute.slang"),
			D(EngineShaderPrograms.GpuDrawMeshUpdate, "GpuDraw/gpu_draw_mesh_update.compute.slang"),
			D(EngineShaderPrograms.GpuDrawTerrainLayerUpdate, "GpuDraw/gpu_draw_terrain_layer_update.compute.slang"),
			D(EngineShaderPrograms.GpuDrawTerrainMaterialUpdate, "GpuDraw/gpu_draw_terrain_material_update.compute.slang"),
			D(EngineShaderPrograms.TerrainRayTracingVertexUpdate, "Terrain/terrain_rt_vertex_update.compute.slang"),
			D(EngineShaderPrograms.Skinning, "Animation/skinning.compute.slang"),
			D(EngineShaderPrograms.TerrainAuthoringBrushes, "Terrain/Tools/terrain_authoring_brushes.compute.slang"),
			D(EngineShaderPrograms.ScreenSpaceDecal, "Geometry/screen_space_decal.slang"),
			D(EngineShaderPrograms.GBufferDecalSeed, "Geometry/gbuffer_decal_seed.compute.slang")
		};

		_byId = new();
		_bySourcePath = new(StringComparer.OrdinalIgnoreCase);
		foreach (var descriptor in descriptors)
		{
			if (_byId.TryAdd(descriptor.Id, descriptor) == false)
				throw new InvalidOperationException($"Duplicate engine shader ID '{descriptor.Id}'.");
			if (_bySourcePath.TryAdd(NormalizePath(descriptor.RelativeSourcePath), descriptor) == false)
				throw new InvalidOperationException($"Duplicate engine shader source '{descriptor.RelativeSourcePath}'.");
		}
	}

	public IReadOnlyCollection<EngineShaderProgramDescriptor> Programs => _byId.Values;

	public EngineShaderProgramDescriptor Get(ShaderProgramId id) =>
		_byId.TryGetValue(id, out var descriptor)
			? descriptor
			: throw new KeyNotFoundException($"Engine shader program '{id}' is not declared in the catalog.");

	public void ValidateRequest(ShaderRequest request)
	{
		Get(request.ProgramId);
		if (request.Kind == ShaderRequestKind.Graphics)
		{
			if (IsGraphicsProgram(request.ProgramId) == false ||
			    IsDeclaredGraphicsEntryPointCombination(request) == false)
				throw new InvalidOperationException($"Shader request '{request}' is not a declared graphics entry-point combination.");
		}
		else if (GetComputeEntryPoints(request.ProgramId).Contains(request.ComputeEntryPoint!, StringComparer.Ordinal) == false)
		{
			throw new InvalidOperationException($"Compute entry point '{request.ComputeEntryPoint}' is not declared for '{request.ProgramId}'.");
		}

		foreach (var define in request.GetDefines())
		{
			var legal = define == "WOLF_ALPHA_CLIP" && SupportsAlphaClip(request.ProgramId) ||
			            request.ProgramId == EngineShaderPrograms.ShadowMap &&
			            (define == "WOLF_SHADOW_CASCADE_INDEX=0" || define == "WOLF_SHADOW_CASCADE_INDEX=1" || define == "WOLF_SHADOW_CASCADE_INDEX=2");
			if (legal == false)
				throw new InvalidOperationException($"Define variant '{define}' is not declared for '{request.ProgramId}'.");
		}
	}

	public ShaderProgramId GetIdBySourcePath(string relativeSourcePath) =>
		_bySourcePath.TryGetValue(NormalizePath(relativeSourcePath), out var descriptor)
			? descriptor.Id
			: throw new KeyNotFoundException($"Engine shader source '{relativeSourcePath}' is not declared in the catalog.");

	public void ValidateSourceTree(string shaderSourceRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(shaderSourceRoot);
		foreach (var descriptor in _byId.Values)
		{
			var fullPath = Path.GetFullPath(Path.Combine(shaderSourceRoot, descriptor.RelativeSourcePath));
			if (fullPath.StartsWith(Path.GetFullPath(shaderSourceRoot), StringComparison.Ordinal) == false || File.Exists(fullPath) == false)
				throw new FileNotFoundException($"Catalogued shader source '{descriptor.RelativeSourcePath}' was not found.", fullPath);
		}
	}

	public IReadOnlyList<ShaderRequest> GetDeclaredRuntimeRequests(GraphicsBackendKind backendKind)
	{
		var requests = new List<ShaderRequest>();
		foreach (var descriptor in _byId.Values.OrderBy(value => value.Id.Value, StringComparer.Ordinal))
		{
			if (descriptor.Id == EngineShaderPrograms.Bc1Compress ||
			    descriptor.Id == EngineShaderPrograms.Bc4Compress || descriptor.Id == EngineShaderPrograms.Bc3Stitch ||
			    descriptor.Id == EngineShaderPrograms.Bc5Stitch || descriptor.Id == EngineShaderPrograms.TerrainAuthoringBrushes)
				continue;
			if (IsGraphicsProgram(descriptor.Id))
			{
				requests.Add(ShaderRequest.Graphics(descriptor.Id, "vertexShader", "fragmentShader", backendKind));
				if (descriptor.Id == EngineShaderPrograms.ImGui && backendKind == GraphicsBackendKind.D3D12)
				{
					requests.Add(ShaderRequest.Graphics(
						descriptor.Id,
						"vertexShader",
						"solidFragmentShader",
						backendKind));
				}
				if (SupportsAlphaClip(descriptor.Id))
					requests.Add(ShaderRequest.Graphics(descriptor.Id, "vertexShader", "fragmentShader", backendKind, "WOLF_ALPHA_CLIP"));
				if (descriptor.Id == EngineShaderPrograms.ShadowMap)
					for (var cascade = 0; cascade < 3; cascade++)
					{
						requests.Add(ShaderRequest.Graphics(descriptor.Id, "vertexShader", "fragmentShader", backendKind,
							$"WOLF_SHADOW_CASCADE_INDEX={cascade}"));
						requests.Add(ShaderRequest.Graphics(descriptor.Id, "vertexShader", "fragmentShader", backendKind,
							"WOLF_ALPHA_CLIP", $"WOLF_SHADOW_CASCADE_INDEX={cascade}"));
					}
			}
			else
			{
				foreach (var entryPoint in GetComputeEntryPoints(descriptor.Id))
					requests.Add(ShaderRequest.Compute(descriptor.Id, entryPoint, backendKind));
			}
		}
		return requests;
	}

	private static EngineShaderProgramDescriptor D(ShaderProgramId id, string source) => new(id, source);
	private static string NormalizePath(string value) => value.Replace('\\', '/').TrimStart('/');

	private static bool IsGraphicsProgram(ShaderProgramId id) =>
		id == EngineShaderPrograms.GBuffer || id == EngineShaderPrograms.ImGui ||
		id == EngineShaderPrograms.TransparentForward || id == EngineShaderPrograms.ShadowMap ||
		id == EngineShaderPrograms.TerrainSharedGBuffer || id == EngineShaderPrograms.DebugPrimitiveForward ||
		id == EngineShaderPrograms.DebugPrimitiveGBuffer || id == EngineShaderPrograms.ScreenSpaceDecal;

	private static bool IsDeclaredGraphicsEntryPointCombination(ShaderRequest request) =>
		request.VertexEntryPoint == "vertexShader" &&
		(request.PixelEntryPoint == "fragmentShader" ||
		 request.ProgramId == EngineShaderPrograms.ImGui &&
		 request.BackendKind == GraphicsBackendKind.D3D12 &&
		 request.PixelEntryPoint == "solidFragmentShader");

	private static bool SupportsAlphaClip(ShaderProgramId id) =>
		id == EngineShaderPrograms.GBuffer || id == EngineShaderPrograms.ShadowMap;

	private static string[] GetComputeEntryPoints(ShaderProgramId id)
	{
		if (id == EngineShaderPrograms.DeferredLighting) return ["DeferredLightingCS"];
		if (id == EngineShaderPrograms.ClusteredLighting) return ["CSBuildClusters", "CSWriteLightIndices"];
		if (id == EngineShaderPrograms.ProceduralSkybox) return ["ProceduralSkyboxCSMain"];
		if (id == EngineShaderPrograms.IblBrdfLut) return ["IblBrdfCSMain"];
		if (id == EngineShaderPrograms.IblIrradiance) return ["IblIrradianceCSMain"];
		if (id == EngineShaderPrograms.IblPrefilter) return ["IblPrefilterCSMain"];
		if (id == EngineShaderPrograms.GpuDrawCull) return ["CSCull"];
		if (id == EngineShaderPrograms.GpuDrawCompact) return ["CSCompact"];
		if (id == EngineShaderPrograms.AmbientOcclusionVbao) return ["AmbientOcclusionVisibilityBitmaskCS"];
		if (id == EngineShaderPrograms.AmbientOcclusionRayTraced) return ["AmbientOcclusionRayTracedCS"];
		if (id == EngineShaderPrograms.ReflectionsScreenSpace) return ["ReflectionsScreenSpaceCS"];
		if (id == EngineShaderPrograms.ReflectionsRayTraced) return ["ReflectionsRayTracedCS"];
		if (id == EngineShaderPrograms.ReflectionsUpsample) return ["ReflectionsUpsampleCS"];
		if (id == EngineShaderPrograms.DdgiClassify) return ["DdgiProbeClassifyCS"];
		if (id == EngineShaderPrograms.DdgiTrace) return ["DdgiProbeTraceCS", "DdgiRelocationTraceCS"];
		if (id == EngineShaderPrograms.DdgiRelocate) return ["DdgiRelocationSolveCS"];
		if (id == EngineShaderPrograms.DdgiIrradianceIntegrate) return ["DdgiIrradianceIntegrateCS"];
		if (id == EngineShaderPrograms.DdgiIntegrate) return ["DdgiVisibilityIntegrateCS"];
		if (id == EngineShaderPrograms.AmbientOcclusionBlur) return ["AmbientOcclusionBlurCS"];
		if (id == EngineShaderPrograms.AmbientOcclusionUpsample) return ["AmbientOcclusionUpsampleCS"];
		if (id == EngineShaderPrograms.TaaResolve) return ["TaaResolveCS"];
		if (id == EngineShaderPrograms.TaaHistoryStore) return ["TaaHistoryStoreCS"];
		if (id == EngineShaderPrograms.Fsr3Rcas) return ["Fsr3RcasCS"];
		if (id == EngineShaderPrograms.Fsr3PrepareInputs) return ["Fsr3PrepareInputsCS"];
		if (id == EngineShaderPrograms.Fsr3LumaPyramid) return ["Fsr3LumaPyramidCS"];
		if (id == EngineShaderPrograms.Fsr3ShadingChangePyramid) return ["Fsr3ShadingChangePyramidCS"];
		if (id == EngineShaderPrograms.Fsr3ShadingChange) return ["Fsr3ShadingChangeCS"];
		if (id == EngineShaderPrograms.Fsr3PrepareReactivity) return ["Fsr3PrepareReactivityCS"];
		if (id == EngineShaderPrograms.Fsr3DebugView) return ["Fsr3DebugViewCS"];
		if (id == EngineShaderPrograms.Fsr3LumaInstability) return ["Fsr3LumaInstabilityCS"];
		if (id == EngineShaderPrograms.Fsr3Accumulate) return ["Fsr3AccumulateCS"];
		if (id == EngineShaderPrograms.Fsr3Clear) return ["Fsr3ClearFloatCS", "Fsr3ClearUintCS"];
		if (id == EngineShaderPrograms.Tonemapping) return ["TonemappingCS"];
		if (id == EngineShaderPrograms.Bloom) return ["BloomPrefilterCS", "BloomDownsampleCS", "BloomUpsampleCS", "BloomCompositeCS"];
		if (id == EngineShaderPrograms.ColorPyramid) return ["ColorPyramidCopyCS", "ColorPyramidDownsampleCS"];
		if (id == EngineShaderPrograms.CasSharpen) return ["CasSharpenCS"];
		if (id == EngineShaderPrograms.CopyToFinal) return ["CopyToFinalCS"];
		if (id == EngineShaderPrograms.MotionVectorDebug) return ["MotionVectorDebugCS"];
		if (id == EngineShaderPrograms.Bc1Compress) return ["CompressBc1"];
		if (id == EngineShaderPrograms.Bc4Compress) return ["CompressBc4"];
		if (id == EngineShaderPrograms.Bc3Stitch) return ["StitchBc3"];
		if (id == EngineShaderPrograms.Bc5Stitch) return ["StitchBc5"];
		if (id == EngineShaderPrograms.GpuDrawInstanceUpdate) return ["CSUpdateInstance"];
		if (id == EngineShaderPrograms.GpuDrawMaterialUpdate) return ["CSUpdateMaterial"];
		if (id == EngineShaderPrograms.GpuDrawMeshUpdate) return ["CSUpdateMesh"];
		if (id == EngineShaderPrograms.GpuDrawTerrainLayerUpdate) return ["CSUpdateTerrainLayer"];
		if (id == EngineShaderPrograms.GpuDrawTerrainMaterialUpdate) return ["CSUpdateTerrainMaterial"];
		if (id == EngineShaderPrograms.TerrainRayTracingVertexUpdate) return ["TerrainRayTracingVertexUpdateCS"];
		if (id == EngineShaderPrograms.Skinning) return ["SkinningCS"];
		if (id == EngineShaderPrograms.TerrainAuthoringBrushes) return ["ApplyHeightmapRaiseLowerBrush", "ApplyHeightmapFlattenBrush", "ApplyHeightmapSmoothBrush", "ApplyLayerMapLayerBrush"];
		if (id == EngineShaderPrograms.GBufferDecalSeed) return ["GBufferDecalSeedCS"];
		return [];
	}
}
