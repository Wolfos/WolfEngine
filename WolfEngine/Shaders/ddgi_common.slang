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
    uint irradianceHistoryReadHandle;
    uint visibilityHistoryReadHandle;
    uint irradianceHistoryWriteHandle;
    uint visibilityHistoryWriteHandle;
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
    float maxRayDistance;
    float normalBias;
    float viewBias;
    float hysteresis;
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

float3 DdgiRayDirection(uint rayIndex, uint rayCount, uint probeIndex)
{
    float sample = float(rayIndex) + 0.5f;
    float count = max(float(rayCount), 1.0f);
    float cosTheta = 1.0f - 2.0f * sample / count;
    float sinTheta = sqrt(saturate(1.0f - cosTheta * cosTheta));
    float phi = (float(rayIndex) * 2.39996322973f) + frac(float(probeIndex) * 0.61803398875f) * 2.0f * WOLF_PI;
    return float3(cos(phi) * sinTheta, cosTheta, sin(phi) * sinTheta);
}

uint DdgiHash(uint3 v)
{
    uint h = v.x * 747796405u + v.y * 2891336453u + v.z * 277803737u + frameIndex * 1597334677u;
    h = ((h >> ((h >> 28u) + 4u)) ^ h) * 277803737u;
    return (h >> 22u) ^ h;
}

float3 DdgiJitterDirection(float3 direction, uint2 pixel, uint probeIndex)
{
    uint h = DdgiHash(uint3(pixel, probeIndex));
    float a = (float(h & 0xFFFFu) / 65535.0f) * 6.28318530718f;
    float r = (float((h >> 16u) & 0xFFFFu) / 65535.0f) * 0.08f;
    float3 helper = abs(direction.y) < 0.999f ? float3(0.0f, 1.0f, 0.0f) : float3(1.0f, 0.0f, 0.0f);
    float3 tangent = normalize(cross(helper, direction));
    float3 bitangent = cross(direction, tangent);
    return normalize(direction + (cos(a) * tangent + sin(a) * bitangent) * r);
}

float3 DdgiSampleSky(float3 direction)
{
    return SampleEnvironment(environmentHandle, samplerHandle, direction);
}
