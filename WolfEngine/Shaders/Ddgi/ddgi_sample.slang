// Shared DDGI probe addressing and irradiance sampling.
//
// Deliberately declares no cbuffer of its own. The DDGI parameters live in a
// different constant buffer in every consumer -- the probe update passes bind
// DdgiSettings at b1, deferred lighting inlines the same values into its
// LightingParams at b2 -- so a header that read a cbuffer directly could only
// ever be included by one of them. That is what produced the duplicate copies
// of this code in the first place. Callers fill DdgiSampleParams from whatever
// binding they have and pass it in.
import "../Common/common_bindless.slang";
import "ddgi_visibility.slang";

static const uint DDGI_VISIBILITY_TILE_INTERIOR_SIZE = 16u;
static const uint DDGI_TILE_BORDER_SIZE = 1u;
static const float DDGI_SH_BASIS_L0 = 0.28209479177f;
static const float DDGI_SH_BASIS_L1 = 0.48860251190f;
static const float DDGI_SH_DIRECTIONAL_LIMIT = 0.95f;

/// Everything the probe sampler needs that would otherwise come from a cbuffer.
struct DdgiSampleParams
{
    float3 origin;
    float probeSpacing;
    uint3 probeCount;
    uint totalProbeCount;
    int3 storageOffset;
    uint atlasColumns;
    uint atlasRows;
    uint tileBorderSize;
    float maxRayDistance;
    float viewBias;
    float horizontalBlendDistance;
    float verticalBlendDistance;
    uint enabled;
    uint probeRelocationEnabled;

    uint irradianceL0Handle;
    uint irradianceLyHandle;
    uint irradianceLzHandle;
    uint irradianceLxHandle;
    uint visibilityHandle;
    uint probeStateHandle;
    uint probeActivityHandle;
    uint relocationDecisionHandle;
    uint samplerHandle;
};

struct DdgiShSample
{
    float3 coefficientL0;
    float3 coefficientLy;
    float3 coefficientLz;
    float3 coefficientLx;
    uint valid;
};

/// Debug channels produced alongside a sample. Filled only when the caller asks
/// for them; every field is zeroed otherwise.
struct DdgiProbeDebug
{
    float baseWeight;
    float weightedVisibility;
    float3 dominantProbe;
    float3 dominantProbeCoord;
    float3 probeRelocation;
    float3 probeRelocationDecision;
};

DdgiProbeDebug DdgiMakeEmptyProbeDebug()
{
    DdgiProbeDebug debug;
    debug.baseWeight = 0.0f;
    debug.weightedVisibility = 0.0f;
    debug.dominantProbe = 0.0f.xxx;
    debug.dominantProbeCoord = 0.0f.xxx;
    debug.probeRelocation = 0.0f.xxx;
    debug.probeRelocationDecision = 0.0f.xxx;
    return debug;
}

int DdgiPositiveModulo(int value, int modulus)
{
    int remainder = value % max(modulus, 1);
    return remainder < 0 ? remainder + modulus : remainder;
}

float3 DdgiOctDecode(float2 f)
{
    float3 n = float3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += select(n.xy >= 0.0f, -t.xx, t.xx);
    return normalize(n);
}

float2 DdgiOctEncode(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 enc = n.xy;
    if (n.z < 0.0f)
    {
        enc = (1.0f - abs(enc.yx)) * select(enc.xy >= 0.0f, 1.0f.xx, -1.0f.xx);
    }
    return enc;
}

uint3 DdgiLogicalCoordFromIndex(uint logicalProbeIndex, uint3 probeCount)
{
    uint x = logicalProbeIndex % max(probeCount.x, 1u);
    uint yz = logicalProbeIndex / max(probeCount.x, 1u);
    uint y = yz % max(probeCount.y, 1u);
    uint z = yz / max(probeCount.y, 1u);
    return uint3(x, y, z);
}

/// Maps a logical probe index to its slot in the scrolling atlas.
uint DdgiPhysicalProbeIndexFromLayout(uint logicalProbeIndex, uint3 probeCount, int3 storageOffset)
{
    uint3 logical = DdgiLogicalCoordFromIndex(logicalProbeIndex, probeCount);
    uint3 physical = uint3(
        DdgiPositiveModulo(int(logical.x) + storageOffset.x, int(probeCount.x)),
        DdgiPositiveModulo(int(logical.y) + storageOffset.y, int(probeCount.y)),
        DdgiPositiveModulo(int(logical.z) + storageOffset.z, int(probeCount.z)));
    return physical.x +
        physical.y * probeCount.x +
        physical.z * probeCount.x * probeCount.y;
}

uint DdgiPhysicalProbeIndex(DdgiSampleParams params, uint logicalProbeIndex)
{
    return DdgiPhysicalProbeIndexFromLayout(logicalProbeIndex, params.probeCount, params.storageOffset);
}

/// Octahedral atlas lookup for an already-resolved physical probe slot.
float2 DdgiProbeAtlasUvFromLayout(
    uint physicalProbeIndex,
    float3 direction,
    uint interiorSize,
    uint borderSize,
    uint atlasColumns,
    uint atlasRows)
{
    uint tileSize = interiorSize + borderSize * 2u;
    uint tileX = physicalProbeIndex % max(atlasColumns, 1u);
    uint tileY = physicalProbeIndex / max(atlasColumns, 1u);
    float2 interiorUv = DdgiOctEncode(normalize(direction)) * 0.5f + 0.5f;
    float2 atlasPixel = float2(tileX * tileSize + borderSize, tileY * tileSize + borderSize) +
        interiorUv * float(interiorSize - 1u) + 0.5f.xx;
    return atlasPixel / float2(atlasColumns * tileSize, atlasRows * tileSize);
}

float2 DdgiProbeAtlasUv(DdgiSampleParams params, uint logicalProbeIndex, float3 direction, uint interiorSize)
{
    return DdgiProbeAtlasUvFromLayout(
        DdgiPhysicalProbeIndex(params, logicalProbeIndex),
        direction,
        interiorSize,
        params.tileBorderSize,
        params.atlasColumns,
        params.atlasRows);
}

float3 DdgiProbePosition(DdgiSampleParams params, uint3 probeCoord)
{
    return params.origin + float3(probeCoord) * params.probeSpacing;
}

uint2 DdgiCoefficientPixel(DdgiSampleParams params, uint physicalProbeIndex)
{
    uint columns = max(params.atlasColumns, 1u);
    return uint2(physicalProbeIndex % columns, physicalProbeIndex / columns);
}

/// Fades the probe volume out near its boundary so surfaces leaving the grid do
/// not pop between probe lighting and the sky fallback.
float DdgiGridInfluence(DdgiSampleParams params, float3 worldPos)
{
    if (params.enabled == 0u || params.totalProbeCount == 0u)
    {
        return 0.0f;
    }

    float spacing = max(params.probeSpacing, 0.001f);
    float3 probeMin = params.origin;
    float3 probeMax = params.origin + float3(
        max(params.probeCount.x, 1u) - 1u,
        max(params.probeCount.y, 1u) - 1u,
        max(params.probeCount.z, 1u) - 1u) * spacing;
    float3 volumeMin = probeMin - 0.5f.xxx * spacing;
    float3 volumeMax = probeMax + 0.5f.xxx * spacing;
    float3 distanceInsideGrid = min(worldPos - volumeMin, volumeMax - worldPos);
    if (any(distanceInsideGrid < 0.0f.xxx))
    {
        return 0.0f;
    }

    float horizontalDistanceToBoundary = min(distanceInsideGrid.x, distanceInsideGrid.z);
    float horizontalInfluence = smoothstep(
        0.0f,
        max(params.horizontalBlendDistance, 0.001f),
        horizontalDistanceToBoundary);
    float verticalDistanceToTopBoundary = volumeMax.y - worldPos.y;
    float verticalInfluence = smoothstep(
        0.0f,
        max(params.verticalBlendDistance, 0.001f),
        verticalDistanceToTopBoundary);
    return min(horizontalInfluence, verticalInfluence);
}

/// Trilinearly blends the eight surrounding probes, rejecting probes that are
/// disabled, unconverged, or occluded from the shading point.
DdgiShSample SampleDdgiSh(
    DdgiSampleParams params,
    float3 worldPos,
    float3 normal,
    float gridInfluence,
    uint probeDebugEnabled,
    out DdgiProbeDebug debug)
{
    DdgiShSample result = (DdgiShSample)0;
    debug = DdgiMakeEmptyProbeDebug();
    if (gridInfluence <= 0.0f)
    {
        return result;
    }

    float3 biasedPos = worldPos + normal * params.viewBias;
    float3 grid = (biasedPos - params.origin) / max(params.probeSpacing, 0.001f);
    float3 gridMax = float3(
        max(params.probeCount.x, 1u) - 1u,
        max(params.probeCount.y, 1u) - 1u,
        max(params.probeCount.z, 1u) - 1u);
    float3 sampleGrid = clamp(grid, 0.0f.xxx, gridMax);
    float3 baseFloat = floor(sampleGrid);
    float3 fracPart = saturate(sampleGrid - baseFloat);
    int3 baseCoord = int3(baseFloat);
    Texture2D<float4> irradianceL0Tex = GetTexture(params.irradianceL0Handle);
    Texture2D<float4> irradianceLyTex = GetTexture(params.irradianceLyHandle);
    Texture2D<float4> irradianceLzTex = GetTexture(params.irradianceLzHandle);
    Texture2D<float4> irradianceLxTex = GetTexture(params.irradianceLxHandle);
    Texture2D<float4> visibilityTex = GetTexture(params.visibilityHandle);
    Texture2D<float4> probeStateTex = GetTexture(params.probeStateHandle);
    Texture2D<float4> probeActivityTex = GetTexture(params.probeActivityHandle);
    Texture2D<float4> relocationDecisionTex = GetTexture(params.relocationDecisionHandle);
    SamplerState linearSampler = GetSampler(params.samplerHandle);

    float3 coefficientL0 = 0.0f.xxx;
    float3 coefficientLy = 0.0f.xxx;
    float3 coefficientLz = 0.0f.xxx;
    float3 coefficientLx = 0.0f.xxx;
    float baseWeightSum = 0.0f;
    float weightSum = 0.0f;
    float dominantProbeWeight = 0.0f;
    uint dominantProbeIndex = 0u;
    float3 dominantProbeCoord = 0.0f.xxx;
    float3 relocationDebugSum = 0.0f.xxx;
    float3 relocationDecisionDebugSum = 0.0f.xxx;
    for (uint z = 0u; z <= 1u; z++)
    {
        for (uint y = 0u; y <= 1u; y++)
        {
            for (uint x = 0u; x <= 1u; x++)
            {
                int3 coord = baseCoord + int3(x, y, z);
                if (coord.x < 0 || coord.y < 0 || coord.z < 0 ||
                    coord.x >= int(params.probeCount.x) ||
                    coord.y >= int(params.probeCount.y) ||
                    coord.z >= int(params.probeCount.z))
                {
                    continue;
                }

                uint probeIndex = uint(coord.x) +
                    uint(coord.y) * params.probeCount.x +
                    uint(coord.z) * params.probeCount.x * params.probeCount.y;
                float3 tri = lerp(1.0f.xxx - fracPart, fracPart, float3(x, y, z));
                float trilinearWeight = tri.x * tri.y * tri.z;
                uint physicalProbeIndex = DdgiPhysicalProbeIndex(params, probeIndex);
                uint2 probePixel = DdgiCoefficientPixel(params, physicalProbeIndex);
                if (probeActivityTex.Load(int3(probePixel, 0)).x <= 0.5f)
                {
                    continue;
                }
                float4 probeState = probeStateTex.Load(int3(probePixel, 0));
                if (uint(round(max(probeState.w, 0.0f))) != 1u)
                {
                    continue;
                }
                float3 relocationOffset =
                    params.probeRelocationEnabled != 0u
                    ? probeState.xyz
                    : 0.0f.xxx;
                float3 probePosition = DdgiProbePosition(params, uint3(coord)) + relocationOffset;
                float3 toSurface = biasedPos - probePosition;
                float distanceToProbe = length(toSurface);
                float3 direction = distanceToProbe > 1e-4f ? toSurface / distanceToProbe : normal;
                // A wrapped-cosine weight keeps nearby tangent probes available
                // while strongly rejecting probes on the back side of a surface.
                // The old linear 10% floor let probes outside thin walls retain
                // enough weight to inject sky radiance into enclosed surfaces.
                float wrappedNormal = saturate(dot(normal, -direction) * 0.5f + 0.5f);
                float nWeight = wrappedNormal * wrappedNormal;
                float weight = trilinearWeight * nWeight;
                float2 visibilityUv = DdgiProbeAtlasUvFromLayout(
                    physicalProbeIndex,
                    direction,
                    DDGI_VISIBILITY_TILE_INTERIOR_SIZE,
                    params.tileBorderSize,
                    params.atlasColumns,
                    params.atlasRows);
                float2 moments = visibilityTex.SampleLevel(linearSampler, visibilityUv, 0.0f).rg;
                float normalizedDistance = saturate(distanceToProbe / max(params.maxRayDistance, 0.001f));
                float meanDistance = moments.x;
                float meanDistanceSquared = moments.y;
                float visibility = DdgiEvaluateVisibility(meanDistance, meanDistanceSquared, normalizedDistance);
                float probeWeight = weight * visibility;
                baseWeightSum += weight;
                if (probeDebugEnabled != 0u && probeWeight > dominantProbeWeight)
                {
                    dominantProbeWeight = probeWeight;
                    dominantProbeIndex = physicalProbeIndex;
                    dominantProbeCoord = float3(coord.x, coord.y, coord.z);
                }
                if (probeDebugEnabled != 0u)
                {
                    relocationDebugSum += relocationOffset * probeWeight;
                    relocationDecisionDebugSum +=
                        relocationDecisionTex.Load(int3(probePixel, 0)).rgb * probeWeight;
                }
                coefficientL0 += irradianceL0Tex.Load(int3(probePixel, 0)).rgb * probeWeight;
                coefficientLy += irradianceLyTex.Load(int3(probePixel, 0)).rgb * probeWeight;
                coefficientLz += irradianceLzTex.Load(int3(probePixel, 0)).rgb * probeWeight;
                coefficientLx += irradianceLxTex.Load(int3(probePixel, 0)).rgb * probeWeight;
                weightSum += probeWeight;
            }
        }
    }

    if (weightSum <= 1e-5f)
    {
        if (probeDebugEnabled != 0u)
        {
            debug.baseWeight = saturate(baseWeightSum);
        }
        return result;
    }

    float inverseWeight = rcp(weightSum);
    if (probeDebugEnabled != 0u)
    {
        debug.baseWeight = saturate(baseWeightSum);
        debug.weightedVisibility = saturate(weightSum / max(baseWeightSum, 1e-5f));
        uint probeHash = dominantProbeIndex * 747796405u + 2891336453u;
        probeHash = ((probeHash >> ((probeHash >> 28u) + 4u)) ^ probeHash) * 277803737u;
        probeHash = (probeHash >> 22u) ^ probeHash;
        float3 probeColor = float3(
            float(probeHash & 0xffu),
            float((probeHash >> 8u) & 0xffu),
            float((probeHash >> 16u) & 0xffu)) / 255.0f;
        probeColor = 0.25f.xxx + 0.75f * probeColor;
        debug.dominantProbe = probeColor * saturate(dominantProbeWeight / weightSum);
        float3 probeCoordMax = max(float3(
            params.probeCount.x,
            params.probeCount.y,
            params.probeCount.z) - 1.0f.xxx, 1.0f.xxx);
        debug.dominantProbeCoord = saturate(dominantProbeCoord / probeCoordMax);

        float3 averageRelocationOffset = relocationDebugSum * inverseWeight;
        float signedVerticalRelocation =
            averageRelocationOffset.y / max(params.probeSpacing, 0.001f) * 0.5f + 0.5f;
        debug.probeRelocation = float3(0.5f, signedVerticalRelocation, 0.5f);
        debug.probeRelocationDecision = relocationDecisionDebugSum * inverseWeight;
    }
    result.coefficientL0 = coefficientL0 * inverseWeight;
    result.coefficientLy = coefficientLy * inverseWeight;
    result.coefficientLz = coefficientLz * inverseWeight;
    result.coefficientLx = coefficientLx * inverseWeight;
    result.valid = 1u;
    return result;
}

float DdgiShLobeScale(float3 dc, float3 directionalX, float3 directionalY, float3 directionalZ)
{
    float3 directionalAmplitude = sqrt(
        directionalX * directionalX +
        directionalY * directionalY +
        directionalZ * directionalZ);
    float3 channelScale = select(
        directionalAmplitude > 1e-5f.xxx,
        dc * DDGI_SH_DIRECTIONAL_LIMIT / max(directionalAmplitude, 1e-5f.xxx),
        1.0f.xxx);
    return min(1.0f, min(channelScale.x, min(channelScale.y, channelScale.z)));
}

float3 EvaluateDdgiDiffuse(DdgiShSample sample, float3 normal)
{
    // Integration stores an L1 representation of cosine-filtered irradiance / PI.
    // Reconstruct it directly rather than applying a second cosine convolution.
    // Limit the directional lobe before evaluation so truncated L1 ringing cannot
    // drive individual color channels negative and create black or tinted bands.
    float3 dc = max(sample.coefficientL0 * DDGI_SH_BASIS_L0, 0.0f.xxx);
    float directionalScale = DDGI_SH_BASIS_L1;
    float3 directionalX = sample.coefficientLx * directionalScale;
    float3 directionalY = sample.coefficientLy * directionalScale;
    float3 directionalZ = sample.coefficientLz * directionalScale;
    float lobeScale = DdgiShLobeScale(dc, directionalX, directionalY, directionalZ);
    float3 signedDirectional = lobeScale * (
        directionalY * normal.y +
        directionalZ * normal.z +
        directionalX * normal.x);
    float3 irradiance = dc + signedDirectional;
    return max(irradiance, 0.0f.xxx);
}

float3 EvaluateDdgiRadiance(DdgiShSample sample, float3 direction)
{
    direction = length(direction) > 1e-5f ? normalize(direction) : float3(0.0f, 0.0f, 1.0f);
    float3 dc = max(sample.coefficientL0 * DDGI_SH_BASIS_L0, 0.0f.xxx);
    float3 directionalX = sample.coefficientLx * DDGI_SH_BASIS_L1;
    float3 directionalY = sample.coefficientLy * DDGI_SH_BASIS_L1;
    float3 directionalZ = sample.coefficientLz * DDGI_SH_BASIS_L1;
    float lobeScale = DdgiShLobeScale(dc, directionalX, directionalY, directionalZ);
    float3 signedDirectional = lobeScale * (
        directionalY * direction.y +
        directionalZ * direction.z +
        directionalX * direction.x);
    return max(dc + signedDirectional, 0.0f.xxx);
}
