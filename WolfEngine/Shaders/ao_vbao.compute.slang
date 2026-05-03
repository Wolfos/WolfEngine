// Ported from https://www.shadertoy.com/view/4cdfzf
import "common.slang";
import "constants.slang";
import "common_bindless.slang";
import "common_lighting.slang";

static const uint AO_SECTOR_COUNT = 32u;

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint outputHandle;
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
    float invProjScaleX;
    float invProjScaleY;
    float projZBias;
    float projZScale;
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

float2 PixelToNdc(float2 pixel, uint2 size)
{
    float2 sizeF = float2(size);
    return float2(
        pixel.x / sizeF.x * 2.0f - 1.0f,
        -(pixel.y / sizeF.y * 2.0f - 1.0f));
}

float ReconstructViewDepth(float depth)
{
    float denom = depth - projZBias;
    if (abs(denom) < 1e-6f)
    {
        denom = denom < 0.0f ? -1e-6f : 1e-6f;
    }

    return projZScale / denom;
}

float3 ReconstructViewPositionFast(int2 pixel, uint2 size, float depth)
{
    float2 ndc = PixelToNdc(float2(pixel) + 0.5f.xx, size);
    float viewZ = ReconstructViewDepth(depth);
    return float3(
        ndc.x * viewZ * invProjScaleX,
        ndc.y * viewZ * invProjScaleY,
        viewZ);
}

float ComputeProjectedRadius(float viewDepth, uint height)
{
    float projY = abs(1.0f / max(camera.invProjection[1][1], 1e-5f));
    return max(radius * projY * float(height) * 0.5f / max(abs(viewDepth), 1e-3f), 1.0f);
}

uint IntervalMask01(float2 interval01)
{
    float minValue = saturate(min(interval01.x, interval01.y));
    float maxValue = saturate(max(interval01.x, interval01.y));
    uint firstSector = min(uint(floor(minValue * float(AO_SECTOR_COUNT))), AO_SECTOR_COUNT);
    uint endSector = min(uint(ceil(maxValue * float(AO_SECTOR_COUNT))), AO_SECTOR_COUNT);
    if (endSector <= firstSector)
    {
        return 0u;
    }

    uint sectorCount = endSector - firstSector;
    if (sectorCount >= AO_SECTOR_COUNT)
    {
        return 0xFFFFFFFFu;
    }

    return ((1u << sectorCount) - 1u) << firstSector;
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
    if (IsBackgroundDepth(centerDepth))
    {
        outputTexture[dispatchThreadId.xy] = float4(1.0f, 1.0f, 1.0f, 1.0f);
        return;
    }

    int2 centerPixel = ClampPixel(int2(uv * float2(fullSize)), fullSize);
    float3 centerViewPos = ReconstructViewPositionFast(centerPixel, fullSize, centerDepth);
    float3 centerWorldNormal = LoadWorldNormalNearest(normalTexture, uv, fullSize);
    float3 centerViewNormal = normalize(mul(float4(centerWorldNormal, 0.0f), camera.viewMatrix).xyz);
    float3 viewDir = normalize(-centerViewPos);

    float noise = InterleavedGradientNoise(dispatchThreadId.xy);
    float radialJitter = frac(noise * 1.61803398875f);
    float sectorJitter = frac(noise * 52.9829189f) * (1.0f / float(AO_SECTOR_COUNT));
    float baseRotation = noise * PI;
    float projectedRadius = ComputeProjectedRadius(centerViewPos.z, fullSize.y);
    float radiusSq = radius * radius;
    float visibility = 0.0f;
    float stepScale = pow(max(projectedRadius, 1.0f), rcp(max(float(stepCount), 1.0f)));

    for (uint sliceIndex = 0u; sliceIndex < sliceCount; ++sliceIndex)
    {
        float sliceAngle = baseRotation + (float(sliceIndex) + 0.5f) * (PI / float(sliceCount));
        float2 direction = float2(cos(sliceAngle), sin(sliceAngle));
        float3 sliceDir = normalize(float3(direction.x * invProjScaleX, -direction.y * invProjScaleY, 0.0f));
        float3 sliceNormal = cross(viewDir, sliceDir);
        float sliceNormalLenSq = dot(sliceNormal, sliceNormal);
        if (sliceNormalLenSq <= 1e-8f)
        {
            visibility += 1.0f;
            continue;
        }

        sliceNormal *= rsqrt(sliceNormalLenSq);
        float3 projectedNormal = centerViewNormal - sliceNormal * dot(centerViewNormal, sliceNormal);
        float projectedNormalLenSq = dot(projectedNormal, projectedNormal);
        if (projectedNormalLenSq <= 1e-8f)
        {
            visibility += 1.0f;
            continue;
        }

        float projectedNormalRcpLen = rsqrt(projectedNormalLenSq);
        projectedNormal *= projectedNormalRcpLen;
        float3 tangent = cross(sliceNormal, projectedNormal);
        float sinN = dot(tangent, viewDir);
        uint visibilityMask = 0u;
        float sampleDistance = max(stepScale, 1.0f);
        if (projectedRadius > 1.0f)
        {
            sampleDistance = pow(stepScale, radialJitter);
        }

        for (uint stepIndex = 1u; stepIndex <= stepCount; ++stepIndex)
        {
            [unroll]
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float2 offsetPixels = direction * (sampleDistance * float(sign));
                float2 sampleUv = uv + offsetPixels / float2(fullSize);
                if (sampleUv.x <= 0.0f || sampleUv.x >= 1.0f || sampleUv.y <= 0.0f || sampleUv.y >= 1.0f)
                {
                    continue;
                }

                float sampleDepth = LoadDepthNearest(depthTexture, sampleUv, fullSize);
                if (IsBackgroundDepth(sampleDepth))
                {
                    continue;
                }

                int2 samplePixel = ClampPixel(int2(sampleUv * float2(fullSize)), fullSize);
                float3 sampleViewPos = ReconstructViewPositionFast(samplePixel, fullSize, sampleDepth);
                float3 delta = sampleViewPos - centerViewPos;
                float distanceSq = dot(delta, delta);
                if (distanceSq <= 1e-8f || distanceSq > radiusSq)
                {
                    continue;
                }

                float invDistance = rsqrt(distanceSq);
                float3 deltaFront = delta * invDistance;
                float3 deltaBack = delta - viewDir * thickness;
                float backDistanceSq = dot(deltaBack, deltaBack);
                if (backDistanceSq <= 1e-8f)
                {
                    continue;
                }

                float backInvDistance = rsqrt(backDistanceSq);
                float3 deltaBackDir = deltaBack * backInvDistance;
                float frontAlignment = dot(centerViewNormal, deltaFront);
                float backAlignment = dot(centerViewNormal, deltaBackDir);
                if (max(frontAlignment, backAlignment) <= bias)
                {
                    continue;
                }

                float2 horizonCos = float2(dot(deltaFront, viewDir), dot(deltaBackDir, viewDir));
                if (sign < 0)
                {
                    horizonCos = horizonCos.yx;
                }

                float d05 = float(sign) * 0.5f;
                float2 horizon01 = ((0.5f + 0.5f * sinN) + d05) - d05 * horizonCos;
                horizon01 += sectorJitter.xx;
                visibilityMask |= IntervalMask01(horizon01);
            }

            if (visibilityMask == 0xFFFFFFFFu)
            {
                break;
            }

            sampleDistance *= stepScale;
        }

        visibility += 1.0f - float(countbits(visibilityMask)) / float(AO_SECTOR_COUNT);
    }

    visibility /= max(float(sliceCount), 1.0f);
    float ao = pow(saturate(1.0f - (1.0f - visibility) * strength), power);
    outputTexture[dispatchThreadId.xy] = float4(ao, ao, ao, 1.0f);
}
