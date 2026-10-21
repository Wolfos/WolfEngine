import "../Common/common.slang";
import "../Common/constants.slang";
import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Common/draw_data.slang";
import "../Common/normal_decode.slang";
import "../Common/packed_vertex.slang";
import "../Common/raytracing_common.slang";
import "../Common/rt_hit_surface.slang";
import "ddgi_sample.slang";

// DDGI final gather: traces each pixel's first diffuse bounce and samples the probes where it lands,
// instead of interpolating probes at the visible surface. The probe grid is far coarser than the
// geometry, so probes on the far side of a wall still contribute at the shading point; a traced ray
// cannot pass through the wall, which is what removes the bleeding.
//
// Output is in the same units deferred lighting expects from EvaluateDdgiDiffuse, irradiance / PI,
// with albedo left out so the textures stay sharp under any later filtering. With cosine-weighted
// directions the PDF cancels the cosine and the 1 / PI, so one sample is just the incoming radiance.
// Alpha carries the hit distance for a future denoiser.

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint outputHandle;
    uint environmentHandle;
    uint irradianceHandle;
    uint samplerHandle;
    uint errorTextureHandle;
    uint ddgiIrradianceL0Handle;
    uint ddgiIrradianceLyHandle;
    uint ddgiIrradianceLzHandle;
    uint ddgiIrradianceLxHandle;
    uint ddgiVisibilityHandle;
    uint ddgiProbeStateHandle;
    uint ddgiProbeActivityHandle;
    uint ddgiProbeRelocationDecisionHandle;
};

cbuffer CameraParams : register(b1)
{
    CameraParams camera;
};

cbuffer FinalGatherSettings : register(b2)
{
    uint framebufferSizeX;
    uint framebufferSizeY;
    uint outputSizeX;
    uint outputSizeY;
    uint frameIndex;
    uint sidecarHitShadingAvailable;
    float maxRayDistance;
    float bias;
    float fireflyClamp;
    float3 directLightDirection;
    float3 directLightColorIntensity;
    float finalGatherPad0;
    float3 ddgiOrigin;
    float ddgiProbeSpacing;
    int ddgiStorageOffsetX;
    int ddgiStorageOffsetY;
    int ddgiStorageOffsetZ;
    uint ddgiProbeCountX;
    uint ddgiProbeCountY;
    uint ddgiProbeCountZ;
    uint ddgiProbeCount;
    uint ddgiAtlasColumns;
    uint ddgiAtlasRows;
    uint ddgiVisibilityTileInteriorSize;
    float ddgiMaxRayDistance;
    float ddgiViewBias;
    float ddgiHorizontalBlendDistance;
    float ddgiVerticalBlendDistance;
    uint ddgiProbeRelocationEnabled;
};

RaytracingAccelerationStructure sceneTlas : register(t3);
StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t4);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t5);
StructuredBuffer<uint> g_InstanceIndexToInstanceHandle : register(t6);
StructuredBuffer<GpuMeshData> g_MeshTable : register(t7);
StructuredBuffer<PackedMeshVertex> g_PackedVertices : register(t8);
StructuredBuffer<uint> g_PackedIndices : register(t9);
StructuredBuffer<GpuTerrainMaterialData> g_TerrainMaterialTable : register(t10);
StructuredBuffer<GpuTerrainLayerData> g_TerrainLayerTable : register(t11);
StructuredBuffer<uint> g_InstanceIndexToTerrainRtResolution : register(t12);

RtSceneTables FinalGatherSceneTables()
{
    RtSceneTables tables;
    tables.instances = g_InstanceTable;
    tables.materials = g_MaterialTable;
    tables.instanceIndexToInstanceHandle = g_InstanceIndexToInstanceHandle;
    tables.meshes = g_MeshTable;
    tables.packedVertices = g_PackedVertices;
    tables.packedIndices = g_PackedIndices;
    tables.terrainMaterials = g_TerrainMaterialTable;
    tables.terrainLayers = g_TerrainLayerTable;
    tables.instanceIndexToTerrainRtResolution = g_InstanceIndexToTerrainRtResolution;
    tables.errorTextureHandle = errorTextureHandle;
    return tables;
}

DdgiSampleParams MakeDdgiSampleParams()
{
    DdgiSampleParams params;
    params.origin = ddgiOrigin;
    params.probeSpacing = ddgiProbeSpacing;
    params.probeCount = uint3(ddgiProbeCountX, ddgiProbeCountY, ddgiProbeCountZ);
    params.totalProbeCount = ddgiProbeCount;
    params.storageOffset = int3(ddgiStorageOffsetX, ddgiStorageOffsetY, ddgiStorageOffsetZ);
    params.atlasColumns = ddgiAtlasColumns;
    params.atlasRows = ddgiAtlasRows;
    params.tileBorderSize = DDGI_TILE_BORDER_SIZE;
    params.visibilityTileInteriorSize = ddgiVisibilityTileInteriorSize;
    params.maxRayDistance = ddgiMaxRayDistance;
    params.viewBias = ddgiViewBias;
    params.horizontalBlendDistance = ddgiHorizontalBlendDistance;
    params.verticalBlendDistance = ddgiVerticalBlendDistance;
    params.enabled = 1u;
    params.probeRelocationEnabled = ddgiProbeRelocationEnabled;
    params.irradianceL0Handle = ddgiIrradianceL0Handle;
    params.irradianceLyHandle = ddgiIrradianceLyHandle;
    params.irradianceLzHandle = ddgiIrradianceLzHandle;
    params.irradianceLxHandle = ddgiIrradianceLxHandle;
    params.visibilityHandle = ddgiVisibilityHandle;
    params.probeStateHandle = ddgiProbeStateHandle;
    params.probeActivityHandle = ddgiProbeActivityHandle;
    params.relocationDecisionHandle = ddgiProbeRelocationDecisionHandle;
    params.samplerHandle = samplerHandle;
    return params;
}

float3 ReconstructCameraRelativePosition(float2 uv, float depth)
{
    float4 clip = float4(uv.x * 2.0f - 1.0f, -(uv.y * 2.0f - 1.0f), depth, 1.0f);
    float4 positionH = mul(clip, camera.invViewProjection);
    return positionH.xyz / positionH.w;
}

uint FinalGatherHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7FEB352Du;
    value ^= value >> 15u;
    value *= 0x846CA68Bu;
    value ^= value >> 16u;
    return value;
}

/// One 2D sample per pixel per frame. Each pixel walks the R2 low-discrepancy sequence from its own
/// random offset, so successive frames at a pixel cover the hemisphere evenly instead of clumping,
/// which is what temporal accumulation will need. Neighbouring pixels stay decorrelated.
float2 FinalGatherSample(uint2 pixel, uint frame)
{
    uint seed = FinalGatherHash(pixel.x * 1973u + FinalGatherHash(pixel.y * 9277u));
    float2 offset = float2(
        float(seed & 0xFFFFu) / 65536.0f,
        float(FinalGatherHash(seed) & 0xFFFFu) / 65536.0f);
    return frac(offset + float(frame & 0xFFFFu) * float2(0.75487766625f, 0.56984029100f));
}

float3 CosineHemisphereDirection(float3 normal, float2 u)
{
    // Orthonormal basis without a branch on the normal's orientation (Duff et al. 2017).
    float signZ = normal.z >= 0.0f ? 1.0f : -1.0f;
    float a = -1.0f / (signZ + normal.z);
    float b = normal.x * normal.y * a;
    float3 tangent = float3(1.0f + signZ * normal.x * normal.x * a, signZ * b, -signZ * normal.x);
    float3 bitangent = float3(b, signZ + normal.y * normal.y * a, -normal.y);

    float radius = sqrt(u.x);
    float angle = 6.28318530718f * u.y;
    float3 local = float3(radius * cos(angle), radius * sin(angle), sqrt(max(1.0f - u.x, 0.0f)));
    return normalize(tangent * local.x + bitangent * local.y + normal * local.z);
}

float3 SampleSkyRadiance(float3 direction)
{
    return SampleEnvironment(environmentHandle, samplerHandle, direction) * IBL_EXPOSURE;
}

/// Irradiance / PI arriving at a world position, from the probes inside the grid and the sky
/// irradiance outside it, blended the same way deferred lighting blends them.
float3 SampleIndirectIrradiance(DdgiSampleParams params, float3 worldPosition, float3 normal)
{
    float3 skyIrradiance = GetTexture(irradianceHandle)
        .SampleLevel(GetSampler(samplerHandle), DirToEnvUV(normal), 0.0f).rgb * IBL_EXPOSURE / PI;
    float gridInfluence = DdgiGridInfluence(params, worldPosition);
    if (gridInfluence <= 0.0f)
    {
        return skyIrradiance;
    }

    DdgiProbeDebug debug;
    DdgiShSample sample = SampleDdgiSh(params, worldPosition, normal, gridInfluence, 0u, debug);
    return lerp(skyIrradiance, EvaluateDdgiDiffuse(sample, normal), gridInfluence);
}

/// Radiance travelling back along a ray that left the gather range without hitting anything. The
/// end point is known to be open space, so the probes there are a safe stand-in for the rest of the
/// path. The receiver normal faces back toward the ray origin, which keeps the probe view bias on
/// the side the ray came from.
float3 SampleUnoccludedRadiance(DdgiSampleParams params, float3 endPosition, float3 direction)
{
    float3 skyRadiance = SampleSkyRadiance(direction);
    float gridInfluence = DdgiGridInfluence(params, endPosition);
    if (gridInfluence <= 0.0f)
    {
        return skyRadiance;
    }

    DdgiProbeDebug debug;
    DdgiShSample sample = SampleDdgiSh(params, endPosition, -direction, gridInfluence, 0u, debug);
    return lerp(skyRadiance, EvaluateDdgiRadiance(sample, direction), gridInfluence);
}

float TraceDirectionalVisibility(float3 worldPosition, float3 geometricNormal, float3 lightDirection)
{
    RayDesc ray;
    ray.Origin = worldPosition + geometricNormal * max(bias, 0.001f);
    ray.Direction = lightDirection;
    ray.TMin = 0.001f;
    ray.TMax = 10000.0f;
    return WolfTraceAny(sceneTlas, ray, 0xFF).hit ? 0.0f : 1.0f;
}

/// Outgoing diffuse radiance of a hit surface, lit the same way the DDGI probe rays light theirs:
/// the primary directional light with a traced shadow, plus the probe irradiance at the hit.
float3 ShadeHitSurface(DdgiSampleParams params, RtHitSurface surface)
{
    float3 direct = 0.0f.xxx;
    if (dot(directLightColorIntensity, directLightColorIntensity) > 1e-10f)
    {
        float3 lightDirection = -normalize(directLightDirection);
        float nDotL = saturate(dot(surface.worldNormal, lightDirection));
        if (nDotL > 0.0f)
        {
            float visibility = TraceDirectionalVisibility(
                surface.worldPosition,
                surface.worldGeometricNormal,
                lightDirection);
            direct = directLightColorIntensity * nDotL * visibility / PI;
        }
    }

    float3 indirect = SampleIndirectIrradiance(params, surface.worldPosition, surface.worldNormal);
    return surface.albedo * (direct + indirect) + surface.emissive;
}

[numthreads(8, 8, 1)]
void DdgiFinalGatherCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 outputPixel = dispatchThreadId.xy;
    uint2 outputSize = uint2(outputSizeX, outputSizeY);
    if (any(outputPixel >= outputSize))
    {
        return;
    }

    uint2 size = uint2(framebufferSizeX, framebufferSizeY);
    float2 fullPixelPosition =
        (float2(outputPixel) + 0.5f) * float2(size) / float2(outputSize);
    uint2 pixel = min(uint2(fullPixelPosition), size - 1u);

    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    float depth = GetTexture(depthHandle).Load(int3(pixel, 0)).r;
    if (IsBackgroundDepth(depth))
    {
        outputTexture[outputPixel] = 0.0f.xxxx;
        return;
    }

    float2 screenUv = (float2(pixel) + 0.5f) / float2(size);
    float3 worldPosition = ReconstructCameraRelativePosition(screenUv, depth) + camera.cameraOrigin;
    float3 normal = DecodeNormal(GetTexture(normalHandle).Load(int3(pixel, 0)).xyz);
    float3 direction = CosineHemisphereDirection(normal, FinalGatherSample(outputPixel, frameIndex));

    float launchBias = max(bias, 0.001f);
    RayDesc ray;
    ray.Origin = worldPosition + normal * launchBias;
    ray.Direction = direction;
    ray.TMin = 0.0f;
    ray.TMax = maxRayDistance;
    WolfRayHit hit = WolfTraceClosest(sceneTlas, ray, 0xFF);

    DdgiSampleParams params = MakeDdgiSampleParams();
    float3 radiance;
    float hitDistance = maxRayDistance;
    if (hit.hit)
    {
        hitDistance = hit.distance;
        RtHitSurface surface;
        bool resolved = sidecarHitShadingAvailable != 0u && RtTryResolveHitSurface(
            FinalGatherSceneTables(),
            hit.instanceIndex,
            hit.primitiveIndex,
            hit.barycentrics,
            hit.distance,
            ray.Origin,
            direction,
            surface);
        if (resolved == false)
        {
            // Geometry without a resolvable material still blocks the ray. Treat it as a mid-grey
            // diffuser lit by the probes so it neither leaks nor punches a black hole.
            float3 hitPosition = ray.Origin + direction * hit.distance;
            radiance = 0.5f * SampleIndirectIrradiance(params, hitPosition, -direction);
        }
        else if (dot(surface.worldGeometricNormal, direction) > 0.0f)
        {
            // The ray started inside geometry or reached the back of a closed mesh; nothing lit
            // can be seen from there. The probe trace makes the same call.
            radiance = 0.0f.xxx;
        }
        else
        {
            radiance = ShadeHitSurface(params, surface);
        }
    }
    else
    {
        radiance = SampleUnoccludedRadiance(params, ray.Origin + direction * maxRayDistance, direction);
    }

    radiance = max(radiance, 0.0f.xxx);
    if (fireflyClamp > 0.0f)
    {
        float luminance = dot(radiance, float3(0.2126f, 0.7152f, 0.0722f));
        if (luminance > fireflyClamp)
        {
            radiance *= fireflyClamp / luminance;
        }
    }

    outputTexture[outputPixel] = float4(radiance, hitDistance);
}
