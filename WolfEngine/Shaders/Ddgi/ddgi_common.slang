import "../Common/common_bindless.slang";
import "../Common/common_lighting.slang";
import "../Common/draw_data.slang";
import "ddgi_visibility.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

static const float WOLF_PI = 3.14159265359f;
static const float DDGI_HISTORY_SH_BASIS_L0 = 0.28209479177f;
static const float DDGI_HISTORY_SH_BASIS_L1 = 0.48860251190f;
static const float DDGI_HISTORY_SH_DIRECTIONAL_LIMIT = 0.95f;

cbuffer BindlessHandles : register(b0)
{
    uint traceIrradianceHandle;
    uint traceVisibilityHandle;
    uint traceIrradianceReadHandle;
    uint traceVisibilityReadHandle;
    uint irradianceL0HistoryReadHandle;
    uint irradianceLyHistoryReadHandle;
    uint irradianceLzHistoryReadHandle;
    uint irradianceLxHistoryReadHandle;
    uint visibilityHistoryReadHandle;
    uint irradianceL0HistoryWriteHandle;
    uint irradianceLyHistoryWriteHandle;
    uint irradianceLzHistoryWriteHandle;
    uint irradianceLxHistoryWriteHandle;
    uint visibilityHistoryWriteHandle;
    uint probeStateReadHandle;
    uint probeStateCurrentHandle;
    uint probeRelocationStateReadHandle;
    uint probeStateWriteHandle;
    uint probeActivityReadHandle;
    uint probeActivityWriteHandle;
    uint probeRelocationDecisionHandle;
    uint environmentHandle;
    uint samplerHandle;
    uint errorTextureHandle;
};

cbuffer DdgiSettings : register(b1)
{
    float3 origin;
    float probeSpacing;
    int storageOffsetX;
    int storageOffsetY;
    int storageOffsetZ;
    int scrollDeltaX;
    int scrollDeltaY;
    int scrollDeltaZ;
    uint scrollingPad0;
    uint scrollingPad1;
    uint probeCountX;
    uint probeCountY;
    uint probeCountZ;
    uint probeCount;
    uint atlasColumns;
    uint atlasRows;
    uint irradianceAtlasWidth;
    uint irradianceAtlasHeight;
    uint visibilityAtlasWidth;
    uint visibilityAtlasHeight;
    uint irradianceTileInteriorSize;
    uint visibilityTileInteriorSize;
    uint tileBorderSize;
    uint raysPerProbe;
    uint probeUpdateFrames;
    uint probeUpdateFrameIndex;
    uint forceFullProbeUpdate;
    uint classifyCompactDispatch;
    float maxRayDistance;
    float normalBias;
    float viewBias;
    float irradianceTemporalBlendSpeed;
    float hysteresis;
    uint probeRelocationEnabled;
    float probeMinFrontfaceDistance;
    float probeBackfaceThreshold;
    float probeMaxRelocationDistance;
    float recursiveBounceEnergy;
    float probeSettingsPad1;
    float3 directLightDirection;
    float directLightPad0;
    float3 directLightColorIntensity;
    float directLightPad1;
    uint frameIndex;
    uint historyValid;
    uint sidecarHitShadingAvailable;
    uint activeDrawCommandUpperBound;
    uint debugFirstProbeRelocationReadback;
    uint debugProbeRelocationReadbackIndex;
    uint relocationIteration;
    uint probeSettingsPad2;
};

static const uint DDGI_PROBE_STATE_DISABLED = 0u;
static const uint DDGI_PROBE_STATE_STABLE = 1u;
static const uint DDGI_RELOCATION_RAY_COUNT = 16u;
static const uint DDGI_RELOCATION_ITERATION_COUNT = 4u;

struct DdgiTexelInfo
{
    bool valid;
    uint probeIndex;
    uint2 interiorPixel;
};

float3 DdgiProbePosition(uint probeIndex)
{
    uint x = probeIndex % probeCountX;
    uint yz = probeIndex / probeCountX;
    uint y = yz % probeCountY;
    uint z = yz / probeCountY;
    return origin + float3(float(x), float(y), float(z)) * probeSpacing;
}

int DdgiPositiveModulo(int value, int modulus)
{
    int remainder = value % max(modulus, 1);
    return remainder < 0 ? remainder + modulus : remainder;
}

uint3 DdgiLogicalProbeCoord(uint probeIndex)
{
    uint x = probeIndex % probeCountX;
    uint yz = probeIndex / probeCountX;
    return uint3(x, yz % probeCountY, yz / probeCountY);
}

uint DdgiLogicalProbeIndex(uint3 coord)
{
    return coord.x + coord.y * probeCountX + coord.z * probeCountX * probeCountY;
}

uint3 DdgiPhysicalProbeCoord(uint logicalProbeIndex)
{
    uint3 logical = DdgiLogicalProbeCoord(logicalProbeIndex);
    return uint3(
        DdgiPositiveModulo(int(logical.x) + storageOffsetX, int(probeCountX)),
        DdgiPositiveModulo(int(logical.y) + storageOffsetY, int(probeCountY)),
        DdgiPositiveModulo(int(logical.z) + storageOffsetZ, int(probeCountZ)));
}

uint DdgiPhysicalProbeIndex(uint logicalProbeIndex)
{
    uint3 physical = DdgiPhysicalProbeCoord(logicalProbeIndex);
    return physical.x + physical.y * probeCountX + physical.z * probeCountX * probeCountY;
}

uint DdgiLogicalProbeIndexFromPhysical(uint physicalProbeIndex)
{
    uint x = physicalProbeIndex % probeCountX;
    uint yz = physicalProbeIndex / probeCountX;
    uint3 logical = uint3(
        DdgiPositiveModulo(int(x) - storageOffsetX, int(probeCountX)),
        DdgiPositiveModulo(int(yz % probeCountY) - storageOffsetY, int(probeCountY)),
        DdgiPositiveModulo(int(yz / probeCountY) - storageOffsetZ, int(probeCountZ)));
    return DdgiLogicalProbeIndex(logical);
}

bool DdgiProbeHasHistory(uint logicalProbeIndex)
{
    if (historyValid == 0u)
    {
        return false;
    }

    uint3 logical = DdgiLogicalProbeCoord(logicalProbeIndex);
    int3 previous = int3(logical) + int3(scrollDeltaX, scrollDeltaY, scrollDeltaZ);
    return previous.x >= 0 && previous.x < int(probeCountX) &&
        previous.y >= 0 && previous.y < int(probeCountY) &&
        previous.z >= 0 && previous.z < int(probeCountZ);
}

uint2 DdgiProbeStatePixel(uint probeIndex)
{
    uint physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
    return uint2(physicalProbeIndex % atlasColumns, physicalProbeIndex / atlasColumns);
}

float4 DdgiLoadProbeState(uint handle, uint probeIndex)
{
    Texture2D<float4> stateTex = GetTexture(handle);
    return stateTex.Load(int3(DdgiProbeStatePixel(probeIndex), 0));
}

uint DdgiDecodeProbeState(float encodedState)
{
    return uint(round(max(encodedState, 0.0f)));
}

bool DdgiIsStableProbeState(float4 state)
{
    return DdgiDecodeProbeState(state.w) == DDGI_PROBE_STATE_STABLE;
}

bool DdgiIsProbeEnabled(uint logicalProbeIndex)
{
    Texture2D<float4> activityTex = GetTexture(probeActivityReadHandle);
    return activityTex.Load(int3(DdgiProbeStatePixel(logicalProbeIndex), 0)).x > 0.5f;
}

bool DdgiWasProbeEnabled(uint logicalProbeIndex)
{
    return DdgiProbeHasHistory(logicalProbeIndex) &&
        DdgiDecodeProbeState(DdgiLoadProbeState(probeStateReadHandle, logicalProbeIndex).w) !=
            DDGI_PROBE_STATE_DISABLED;
}

bool DdgiProbeHasUsableHistory(uint logicalProbeIndex)
{
    return DdgiProbeHasHistory(logicalProbeIndex) &&
        DdgiIsStableProbeState(DdgiLoadProbeState(probeStateReadHandle, logicalProbeIndex));
}

float3 DdgiRelocatedProbePosition(uint probeIndex, uint stateHandle, bool stateAvailable)
{
    if (!stateAvailable || probeRelocationEnabled == 0u)
    {
        return DdgiProbePosition(probeIndex);
    }

    return DdgiProbePosition(probeIndex) + DdgiLoadProbeState(stateHandle, probeIndex).xyz;
}

float3 DdgiHistoryProbePosition(uint probeIndex)
{
    return DdgiRelocatedProbePosition(
        probeIndex,
        probeStateReadHandle,
        DdgiProbeHasHistory(probeIndex));
}

float3 DdgiCurrentProbePosition(uint probeIndex)
{
    return DdgiRelocatedProbePosition(probeIndex, probeStateCurrentHandle, true);
}

uint2 DdgiProbeTileOrigin(uint probeIndex, uint interiorSize)
{
    uint tileSize = interiorSize + tileBorderSize * 2u;
    uint physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
    uint tileX = physicalProbeIndex % atlasColumns;
    uint tileY = physicalProbeIndex / atlasColumns;
    return uint2(tileX, tileY) * tileSize;
}

uint DdgiRaySampleCount(uint interiorSize)
{
    return min(max(raysPerProbe, 1u), interiorSize * interiorSize);
}

bool DdgiIsProbeScheduled(uint probeIndex)
{
    uint updateFrames = max(probeUpdateFrames, 1u);
    return forceFullProbeUpdate != 0u ||
        updateFrames == 1u ||
        (probeIndex % updateFrames) == min(probeUpdateFrameIndex, updateFrames - 1u);
}

bool DdgiIsProbeUpdateActive(uint probeIndex)
{
    if (!DdgiIsProbeEnabled(probeIndex))
    {
        return false;
    }

    if (!DdgiIsStableProbeState(DdgiLoadProbeState(probeStateCurrentHandle, probeIndex)))
    {
        return false;
    }

    return
        historyValid == 0u ||
        !DdgiProbeHasHistory(probeIndex) ||
        !DdgiProbeHasUsableHistory(probeIndex) ||
        DdgiIsProbeScheduled(probeIndex);
}

bool DdgiIsProbeRelocationActive(uint probeIndex)
{
    if (probeRelocationEnabled == 0u || !DdgiIsProbeEnabled(probeIndex))
    {
        return false;
    }

    return historyValid == 0u ||
        !DdgiProbeHasHistory(probeIndex) ||
        !DdgiWasProbeEnabled(probeIndex) ||
        DdgiIsProbeScheduled(probeIndex);
}

uint DdgiRaySampleIndex(uint2 interiorPixel, uint interiorSize)
{
    return interiorPixel.y * interiorSize + interiorPixel.x;
}

uint2 DdgiRaySamplePixel(uint probeIndex, uint rayIndex, uint interiorSize)
{
    uint2 tileOrigin = DdgiProbeTileOrigin(probeIndex, interiorSize);
    uint2 local = uint2(rayIndex % interiorSize, rayIndex / interiorSize);
    return tileOrigin + tileBorderSize.xx + local;
}

DdgiTexelInfo DdgiDecodeAtlasTexel(uint2 pixel, uint interiorSize)
{
    DdgiTexelInfo info;
    info.valid = false;
    info.probeIndex = 0u;
    info.interiorPixel = 0u.xx;

    uint tileSize = interiorSize + tileBorderSize * 2u;
    uint2 tile = pixel / tileSize;
    if (tile.x >= atlasColumns || tile.y >= atlasRows)
    {
        return info;
    }

    uint2 local = pixel - tile * tileSize;
    if (local.x < tileBorderSize || local.y < tileBorderSize ||
        local.x >= tileBorderSize + interiorSize || local.y >= tileBorderSize + interiorSize)
    {
        return info;
    }

    uint physicalProbeIndex = tile.y * atlasColumns + tile.x;
    if (physicalProbeIndex >= probeCount)
    {
        return info;
    }

    info.valid = true;
    info.probeIndex = DdgiLogicalProbeIndexFromPhysical(physicalProbeIndex);
    info.interiorPixel = local - tileBorderSize;
    return info;
}

float2 DdgiInteriorUv(uint2 interiorPixel, uint interiorSize)
{
    return ((float2(interiorPixel) + 0.5f) / float(interiorSize)) * 2.0f - 1.0f;
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

float2 DdgiProbeAtlasUv(uint probeIndex, float3 direction, uint interiorSize)
{
    uint tileSize = interiorSize + tileBorderSize * 2u;
    uint physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
    uint tileX = physicalProbeIndex % atlasColumns;
    uint tileY = physicalProbeIndex / atlasColumns;
    float2 interiorUv = DdgiOctEncode(normalize(direction)) * 0.5f + 0.5f;
    float2 atlasPixel = float2(tileX * tileSize + tileBorderSize, tileY * tileSize + tileBorderSize) +
        interiorUv * float(interiorSize - 1u) + 0.5f.xx;
    return atlasPixel / float2(atlasColumns * tileSize, atlasRows * tileSize);
}

float3 DdgiSampleHistory(float3 worldPosition, float3 normal)
{
    if (historyValid == 0u || probeCount == 0u)
    {
        return 0.0f.xxx;
    }

    float spacing = max(probeSpacing, 0.001f);
    normal = length(normal) > 1e-5f ? normalize(normal) : float3(0.0f, 1.0f, 0.0f);
    float3 biasedPosition = worldPosition + normal * viewBias;
    float3 gridMax = float3(
        max(probeCountX, 1u) - 1u,
        max(probeCountY, 1u) - 1u,
        max(probeCountZ, 1u) - 1u);
    float3 gridPosition = (biasedPosition - origin) / spacing;
    if (any(gridPosition < -0.5f.xxx) || any(gridPosition > gridMax + 0.5f.xxx))
    {
        return 0.0f.xxx;
    }

    float3 sampleGrid = clamp(gridPosition, 0.0f.xxx, gridMax);
    float3 baseFloat = floor(sampleGrid);
    float3 fracPart = saturate(sampleGrid - baseFloat);
    int3 baseCoord = int3(baseFloat);
    Texture2D<float4> irradianceL0Tex = GetTexture(irradianceL0HistoryReadHandle);
    Texture2D<float4> irradianceLyTex = GetTexture(irradianceLyHistoryReadHandle);
    Texture2D<float4> irradianceLzTex = GetTexture(irradianceLzHistoryReadHandle);
    Texture2D<float4> irradianceLxTex = GetTexture(irradianceLxHistoryReadHandle);
    Texture2D<float4> visibilityTex = GetTexture(visibilityHistoryReadHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    float3 coefficientL0 = 0.0f.xxx;
    float3 coefficientLy = 0.0f.xxx;
    float3 coefficientLz = 0.0f.xxx;
    float3 coefficientLx = 0.0f.xxx;
    float weightSum = 0.0f;
    for (uint z = 0u; z <= 1u; z++)
    {
        for (uint y = 0u; y <= 1u; y++)
        {
            for (uint x = 0u; x <= 1u; x++)
            {
                int3 coord = baseCoord + int3(x, y, z);
                if (coord.x < 0 || coord.y < 0 || coord.z < 0 ||
                    coord.x >= int(probeCountX) || coord.y >= int(probeCountY) || coord.z >= int(probeCountZ))
                {
                    continue;
                }

                uint probeIndex = uint(coord.x) + uint(coord.y) * probeCountX + uint(coord.z) * probeCountX * probeCountY;
                if (!DdgiProbeHasUsableHistory(probeIndex) || !DdgiIsProbeEnabled(probeIndex))
                {
                    continue;
                }
                float3 tri = lerp(1.0f.xxx - fracPart, fracPart, float3(x, y, z));
                float trilinearWeight = tri.x * tri.y * tri.z;
                float3 probePosition = DdgiHistoryProbePosition(probeIndex);
                float3 toSurface = biasedPosition - probePosition;
                float distanceToProbe = length(toSurface);
                float3 direction = distanceToProbe > 1e-4f ? toSurface / distanceToProbe : normal;
                float wrappedNormal = saturate(dot(normal, -direction) * 0.5f + 0.5f);
                float normalWeight = wrappedNormal * wrappedNormal;
                float2 visibilityUv = DdgiProbeAtlasUv(probeIndex, direction, visibilityTileInteriorSize);
                float2 moments = visibilityTex.SampleLevel(linearSampler, visibilityUv, 0.0f).rg;
                float normalizedDistance = saturate(distanceToProbe / max(maxRayDistance, 0.001f));
                float visibility = DdgiEvaluateVisibility(moments.x, moments.y, normalizedDistance);
                float probeWeight = trilinearWeight * normalWeight * visibility;
                uint2 coefficientPixel = DdgiProbeStatePixel(probeIndex);
                coefficientL0 += irradianceL0Tex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                coefficientLy += irradianceLyTex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                coefficientLz += irradianceLzTex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                coefficientLx += irradianceLxTex.Load(int3(coefficientPixel, 0)).rgb * probeWeight;
                weightSum += probeWeight;
            }
        }
    }

    if (weightSum <= 1e-5f)
    {
        return 0.0f.xxx;
    }

    float inverseWeight = rcp(weightSum);
    coefficientL0 *= inverseWeight;
    coefficientLy *= inverseWeight;
    coefficientLz *= inverseWeight;
    coefficientLx *= inverseWeight;

    float3 dc = max(coefficientL0 * DDGI_HISTORY_SH_BASIS_L0, 0.0f.xxx);
    // Irradiance integration already cosine-filters incident radiance and divides
    // by PI. Reconstruct that stored E/PI function directly; applying the L1
    // clamped-cosine factor here would convolve recursive transport a second time.
    float directionalScale = DDGI_HISTORY_SH_BASIS_L1;
    float3 directionalX = coefficientLx * directionalScale;
    float3 directionalY = coefficientLy * directionalScale;
    float3 directionalZ = coefficientLz * directionalScale;
    float3 directionalAmplitude = sqrt(
        directionalX * directionalX +
        directionalY * directionalY +
        directionalZ * directionalZ);
    float3 channelScale = select(
        directionalAmplitude > 1e-5f.xxx,
        dc * DDGI_HISTORY_SH_DIRECTIONAL_LIMIT / max(directionalAmplitude, 1e-5f.xxx),
        1.0f.xxx);
    float lobeScale = min(1.0f, min(channelScale.x, min(channelScale.y, channelScale.z)));
    float3 directional = lobeScale * (
        directionalY * normal.y +
        directionalZ * normal.z +
        directionalX * normal.x);
    return max(dc + directional, 0.0f.xxx);
}

uint DdgiHash(uint3 v)
{
    uint h = v.x * 747796405u + v.y * 2891336453u + v.z * 277803737u + frameIndex * 1597334677u;
    h = ((h >> ((h >> 28u) + 4u)) ^ h) * 277803737u;
    return (h >> 22u) ^ h;
}

float DdgiHashFloat(uint probeIndex, uint salt)
{
    return (float(DdgiHash(uint3(probeIndex, salt, frameIndex))) + 0.5f) / 4294967296.0f;
}

float4 DdgiRayRotation(uint probeIndex)
{
    uint physicalProbeIndex = DdgiPhysicalProbeIndex(probeIndex);
    float u1 = DdgiHashFloat(physicalProbeIndex, 0x68bc21ebu);
    float u2 = DdgiHashFloat(physicalProbeIndex, 0x02e5be93u);
    float u3 = DdgiHashFloat(physicalProbeIndex, 0x967a889bu);
    float sqrtOneMinusU1 = sqrt(saturate(1.0f - u1));
    float sqrtU1 = sqrt(saturate(u1));
    float angle2 = 2.0f * WOLF_PI * u2;
    float angle3 = 2.0f * WOLF_PI * u3;
    return float4(
        sqrtOneMinusU1 * sin(angle2),
        sqrtOneMinusU1 * cos(angle2),
        sqrtU1 * sin(angle3),
        sqrtU1 * cos(angle3));
}

float3 DdgiRotateDirection(float3 direction, float4 rotation)
{
    return direction +
        2.0f * cross(rotation.xyz, cross(rotation.xyz, direction) + rotation.w * direction);
}

float3 DdgiRayDirection(uint rayIndex, uint rayCount, uint probeIndex)
{
    float sample = float(rayIndex) + 0.5f;
    float count = max(float(rayCount), 1.0f);
    float cosTheta = 1.0f - 2.0f * sample / count;
    float sinTheta = sqrt(saturate(1.0f - cosTheta * cosTheta));
    float phi = float(rayIndex) * 2.39996322973f;
    float3 baseDirection = float3(cos(phi) * sinTheta, cosTheta, sin(phi) * sinTheta);
    return normalize(DdgiRotateDirection(baseDirection, DdgiRayRotation(probeIndex)));
}

float3 DdgiRelocationRayDirection(uint rayIndex)
{
    const float goldenAngle = 2.39996322973f;
    float sampleIndex = float(min(rayIndex, DDGI_RELOCATION_RAY_COUNT - 1u)) + 0.5f;
    float y = 1.0f - 2.0f * sampleIndex / float(DDGI_RELOCATION_RAY_COUNT);
    float radius = sqrt(max(0.0f, 1.0f - y * y));
    float azimuth = goldenAngle * sampleIndex;
    return float3(cos(azimuth) * radius, y, sin(azimuth) * radius);
}

float3 DdgiSampleSky(float3 direction)
{
    return SampleEnvironment(environmentHandle, samplerHandle, direction) * IBL_EXPOSURE;
}
