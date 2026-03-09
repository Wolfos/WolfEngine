import "common.slang";
import "constants.slang";
import "common_bindless.slang";
import "common_lighting.slang";

static const uint AO_SECTOR_COUNT = 32u;
static const float HALF_PI = 1.57079632679f;
static const float TWO_PI = 6.28318530718f;

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer CameraParams : register(b1)
{
    CameraParams camera;
};

cbuffer AoSettings : register(b2)
{
    uint fullResolutionX;
    uint fullResolutionY;
    uint outputResolutionX;
    uint outputResolutionY;
    uint sliceCount;
    uint stepCount;
    float radius;
    float thickness;
    float bias;
    float strength;
    float power;
    float _aoPadding0;
};

float InterleavedGradientNoise(uint2 pixel)
{
    float3 magic = float3(0.06711056f, 0.00583715f, 52.9829189f);
    return frac(magic.z * frac(dot(float2(pixel), magic.xy)));
}

int2 ClampPixel(int2 pixel, uint2 size)
{
    return clamp(pixel, int2(0, 0), int2(int(size.x) - 1, int(size.y) - 1));
}

float LoadDepthNearest(Texture2D<float4> texture, float2 uv, uint2 size)
{
    int2 pixel = ClampPixel(int2(uv * float2(size)), size);
    return texture.Load(int3(pixel, 0)).r;
}

float3 LoadWorldNormalNearest(Texture2D<float4> texture, float2 uv, uint2 size)
{
    int2 pixel = ClampPixel(int2(uv * float2(size)), size);
    return DecodeNormal(texture.Load(int3(pixel, 0)).xyz);
}

float3 LoadViewPositionNearest(Texture2D<float4> depthTexture, float2 uv, uint2 size)
{
    int2 pixel = ClampPixel(int2(uv * float2(size)), size);
    float depth = depthTexture.Load(int3(pixel, 0)).r;
    return ReconstructViewPosition(uint2(pixel), size, depth, camera.invProjection);
}

float ComputeProjectedRadius(float viewDepth, uint height)
{
    float projY = abs(1.0f / max(camera.invProjection[1][1], 1e-5f));
    return max(radius * projY * float(height) * 0.5f / max(abs(viewDepth), 1e-3f), 1.0f);
}

uint IntervalToSectorMask(float minAngle, float maxAngle)
{
    minAngle = clamp(minAngle, 0.0f, HALF_PI);
    maxAngle = clamp(maxAngle, 0.0f, HALF_PI);
    if (maxAngle <= minAngle)
    {
        return 0u;
    }

    uint firstSector = min(uint(floor(minAngle / HALF_PI * float(AO_SECTOR_COUNT))), AO_SECTOR_COUNT - 1u);
    uint lastSector = min(uint(floor(maxAngle / HALF_PI * float(AO_SECTOR_COUNT - 1u))), AO_SECTOR_COUNT - 1u);

    uint mask = 0u;
    for (uint sector = firstSector; sector <= lastSector; ++sector)
    {
        mask |= (1u << sector);
    }

    return mask;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 outputSize = uint2(outputResolutionX, outputResolutionY);
    if (dispatchThreadId.x >= outputSize.x || dispatchThreadId.y >= outputSize.y)
    {
        return;
    }

    uint2 fullSize = uint2(fullResolutionX, fullResolutionY);
    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);

    float2 uv = (float2(dispatchThreadId.xy) + 0.5f) / float2(outputSize);
    float centerDepth = LoadDepthNearest(depthTexture, uv, fullSize);
    if (centerDepth >= 0.9999f)
    {
        outputTexture[dispatchThreadId.xy] = float4(1.0f, 1.0f, 1.0f, 1.0f);
        return;
    }

    int2 centerPixel = ClampPixel(int2(uv * float2(fullSize)), fullSize);
    float3 centerViewPos = ReconstructViewPosition(uint2(centerPixel), fullSize, centerDepth, camera.invProjection);
    float3 centerWorldNormal = LoadWorldNormalNearest(normalTexture, uv, fullSize);
    float3 centerViewNormal = normalize(mul(float4(centerWorldNormal, 0.0f), camera.viewMatrix).xyz);

    float baseRotation = InterleavedGradientNoise(dispatchThreadId.xy) * TWO_PI;
    float projectedRadius = ComputeProjectedRadius(centerViewPos.z, fullSize.y);
    float visibility = 0.0f;

    for (uint sliceIndex = 0u; sliceIndex < sliceCount; ++sliceIndex)
    {
        float sliceAngle = baseRotation + (float(sliceIndex) + 0.5f) * (PI / float(sliceCount));
        float2 direction = float2(cos(sliceAngle), sin(sliceAngle));
        uint visibilityMask = 0u;

        for (uint stepIndex = 1u; stepIndex <= stepCount; ++stepIndex)
        {
            float stepT = float(stepIndex) / float(stepCount);
            float2 offsetPixels = direction * (projectedRadius * stepT);

            [unroll]
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float2 sampleUv = uv + (offsetPixels * float(sign)) / float2(fullSize);
                if (sampleUv.x <= 0.0f || sampleUv.x >= 1.0f || sampleUv.y <= 0.0f || sampleUv.y >= 1.0f)
                {
                    continue;
                }

                float sampleDepth = LoadDepthNearest(depthTexture, sampleUv, fullSize);
                if (sampleDepth >= 0.9999f)
                {
                    continue;
                }

                float3 sampleViewPos = LoadViewPositionNearest(depthTexture, sampleUv, fullSize);
                float3 delta = sampleViewPos - centerViewPos;
                float distance = length(delta);
                if (distance <= 1e-4f || distance > radius)
                {
                    continue;
                }

                float3 dirToSample = delta / distance;
                float normalAlignment = dot(centerViewNormal, dirToSample);
                if (normalAlignment <= bias)
                {
                    continue;
                }

                float angleFromTangent = asin(saturate(normalAlignment));
                float thicknessAngle = atan(thickness / max(distance, 1e-4f));
                float minAngle = max(angleFromTangent - thicknessAngle, 0.0f);
                float maxAngle = min(angleFromTangent + thicknessAngle, HALF_PI);
                visibilityMask |= IntervalToSectorMask(minAngle, maxAngle);
            }
        }

        visibility += 1.0f - float(countbits(visibilityMask)) / float(AO_SECTOR_COUNT);
    }

    visibility /= max(float(sliceCount), 1.0f);
    float ao = pow(saturate(1.0f - (1.0f - visibility) * strength), power);
    outputTexture[dispatchThreadId.xy] = float4(ao, ao, ao, 1.0f);
}
