import "common_bindless.slang";
import "common_lighting.slang";
import "draw_data.slang";

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

static const float WOLF_PI = 3.14159265359f;

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
    uint probeStateWriteHandle;
    uint environmentHandle;
    uint samplerHandle;
};

cbuffer DdgiSettings : register(b1)
{
    float3 origin;
    float probeSpacing;
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
    uint schedulingPad0;
    float maxRayDistance;
    float normalBias;
    float viewBias;
    float irradianceTemporalBlendSpeed;
    float hysteresis;
    uint probeRelocationEnabled;
    float probeMinFrontfaceDistance;
    float probeBackfaceThreshold;
    float probeMaxRelocationDistance;
    float probeSettingsPad0;
    float probeSettingsPad1;
    float3 directLightDirection;
    float directLightPad0;
    float3 directLightColorIntensity;
    float directLightPad1;
    uint frameIndex;
    uint historyValid;
    uint sidecarHitShadingAvailable;
    uint settingsPad0;
};

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

uint2 DdgiProbeStatePixel(uint probeIndex)
{
    return uint2(probeIndex % atlasColumns, probeIndex / atlasColumns);
}

float4 DdgiLoadProbeState(uint handle, uint probeIndex)
{
    Texture2D<float4> stateTex = GetTexture(handle);
    return stateTex.Load(int3(DdgiProbeStatePixel(probeIndex), 0));
}

float3 DdgiRelocatedProbePosition(uint probeIndex, uint stateHandle)
{
    if (historyValid == 0u || probeRelocationEnabled == 0u)
    {
        return DdgiProbePosition(probeIndex);
    }

    return DdgiProbePosition(probeIndex) + DdgiLoadProbeState(stateHandle, probeIndex).xyz;
}

uint2 DdgiProbeTileOrigin(uint probeIndex, uint interiorSize)
{
    uint tileSize = interiorSize + tileBorderSize * 2u;
    uint tileX = probeIndex % atlasColumns;
    uint tileY = probeIndex / atlasColumns;
    return uint2(tileX, tileY) * tileSize;
}

uint DdgiRaySampleCount(uint interiorSize)
{
    return min(max(raysPerProbe, 1u), interiorSize * interiorSize);
}

bool DdgiIsProbeActive(uint probeIndex)
{
    uint updateFrames = max(probeUpdateFrames, 1u);
    return forceFullProbeUpdate != 0u ||
        historyValid == 0u ||
        updateFrames == 1u ||
        (probeIndex % updateFrames) == min(probeUpdateFrameIndex, updateFrames - 1u);
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

    uint probeIndex = tile.y * atlasColumns + tile.x;
    if (probeIndex >= probeCount)
    {
        return info;
    }

    info.valid = true;
    info.probeIndex = probeIndex;
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
    float u1 = DdgiHashFloat(probeIndex, 0x68bc21ebu);
    float u2 = DdgiHashFloat(probeIndex, 0x02e5be93u);
    float u3 = DdgiHashFloat(probeIndex, 0x967a889bu);
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

float3 DdgiSampleSky(float3 direction)
{
    return SampleEnvironment(environmentHandle, samplerHandle, direction) * IBL_EXPOSURE;
}
