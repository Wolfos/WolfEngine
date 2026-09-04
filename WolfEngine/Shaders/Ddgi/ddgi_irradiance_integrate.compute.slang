import "../Common/common_bindless.slang";
import "ddgi_common.slang";

static const uint DDGI_DIRECTION_RESOLUTION = 8u;
static const uint DDGI_DIRECTION_COUNT = DDGI_DIRECTION_RESOLUTION * DDGI_DIRECTION_RESOLUTION;
static const float DDGI_SH_BASIS_L0 = 0.28209479177f;
static const float DDGI_SH_BASIS_L1 = 0.48860251190f;
// Temporal rates are authored per rendered frame, but a probe may only run once
// every probeUpdateFrames frames. Convert them to an equivalent per-update rate
// so sparse scheduling does not make persistent lighting converge arbitrarily slowly.
static const float DDGI_MEAN_BLEND_PER_FRAME = 0.02f;

struct DdgiVarianceData
{
    float3 mean;
    float3 shortMean;
    float vbbr;
    float3 variance;
    float inconsistency;
};

RWStructuredBuffer<uint4> g_IrradianceEstimator : register(u2);
groupshared uint g_FilteredRadiance[DDGI_DIRECTION_COUNT];
groupshared float3 g_TraceRadiance[DDGI_DIRECTION_COUNT];
groupshared float3 g_TraceDirections[DDGI_DIRECTION_COUNT];

uint PackHalf2(float2 value)
{
    return f32tof16(value.x) | (f32tof16(value.y) << 16u);
}

float2 UnpackHalf2(uint value)
{
    return float2(f16tof32(value), f16tof32(value >> 16u));
}

uint PackRgbe(float3 rgb)
{
    const float maxValue = asfloat(0x477F8000);
    const float minValue = asfloat(0x37800000);
    rgb = clamp(rgb, 0.0f.xxx, maxValue.xxx);
    float maxChannel = max(max(minValue, rgb.r), max(rgb.g, rgb.b));
    float bias = asfloat((asuint(maxChannel) + 0x07804000u) & 0x7F800000u);
    uint3 packedRgb = asuint(rgb + bias.xxx);
    uint exponent = (asuint(bias) << 4u) + 0x10000000u;
    return exponent | (packedRgb.b << 18u) | (packedRgb.g << 9u) | (packedRgb.r & 0x1FFu);
}

float3 UnpackRgbe(uint packed)
{
    float3 rgb = float3(uint3(packed, packed >> 9u, packed >> 18u) & 0x1FFu);
    return ldexp(rgb, int(packed >> 27u) - 24);
}

DdgiVarianceData LoadVarianceData(uint index)
{
    uint4 packed = g_IrradianceEstimator[index];
    DdgiVarianceData data;
    data.mean = UnpackRgbe(packed.x);
    data.shortMean = UnpackRgbe(packed.y);
    data.variance = UnpackRgbe(packed.z);
    float2 other = UnpackHalf2(packed.w);
    data.vbbr = other.x;
    data.inconsistency = other.y;
    return data;
}

void StoreVarianceData(uint index, DdgiVarianceData data)
{
    g_IrradianceEstimator[index] = uint4(
        PackRgbe(data.mean),
        PackRgbe(data.shortMean),
        PackRgbe(data.variance),
        PackHalf2(float2(data.vbbr, data.inconsistency)));
}

float DdgiCadenceAdjustedBlend(float perFrameBlend)
{
    float clampedBlend = saturate(perFrameBlend);
    return 1.0f - pow(1.0f - clampedBlend, float(max(probeUpdateFrames, 1u)));
}

void MultiscaleMeanEstimator(float3 sampleValue, inout DdgiVarianceData data)
{
    float3 deviation = sqrt(max(1e-5f.xxx, data.variance));
    float3 highThreshold = 0.1f.xxx + data.shortMean + deviation * 8.0f;
    sampleValue -= max(0.0f.xxx, sampleValue - highThreshold);

    float shortMeanBlend = DdgiCadenceAdjustedBlend(irradianceTemporalBlendSpeed);
    float varianceBlend = DdgiCadenceAdjustedBlend(irradianceTemporalBlendSpeed * 0.5f);
    float3 delta = sampleValue - data.shortMean;
    data.shortMean = lerp(data.shortMean, sampleValue, shortMeanBlend);
    float3 delta2 = sampleValue - data.shortMean;
    data.variance = lerp(
        data.variance,
        max(0.0f.xxx, delta * delta2),
        varianceBlend);
    deviation = sqrt(max(1e-5f.xxx, data.variance));

    float relativeDifference = dot(
        float3(0.299f, 0.587f, 0.114f),
        abs(data.mean - data.shortMean) / max(1e-5f.xxx, deviation));
    data.inconsistency = lerp(
        data.inconsistency,
        relativeDifference,
        DdgiCadenceAdjustedBlend(0.08f));

    float varianceBlendReduction = clamp(
        dot(
            float3(0.299f, 0.587f, 0.114f),
            0.5f * data.shortMean / max(1e-5f.xxx, deviation)),
        1.0f / 32.0f,
        1.0f);
    data.vbbr = lerp(
        data.vbbr,
        varianceBlendReduction,
        DdgiCadenceAdjustedBlend(0.1f));

    // The short mean is the robust sample estimate: the high-side clamp above
    // absorbs isolated fireflies before they reach it. Track that filtered value
    // independently at a cadence-adjusted rate instead of applying a 2% ceiling
    // once per scheduled probe update. The old coupling made recursive feedback
    // take minutes to approach its fixed point when probes updated every 16 frames.
    data.mean = lerp(
        data.mean,
        data.shortMean,
        DdgiCadenceAdjustedBlend(DDGI_MEAN_BLEND_PER_FRAME));
}

float3 IntegrateDirection(float3 direction, uint rayCount)
{
    float3 radiance = 0.0f.xxx;
    float weightSum = 0.0f;
    for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
    {
        float weight = saturate(dot(direction, g_TraceDirections[rayIndex]));
        radiance += g_TraceRadiance[rayIndex] * weight;
        weightSum += weight;
    }
    return weightSum > 1e-5f ? radiance / weightSum : 0.0f.xxx;
}

void CopyHistory(uint probeIndex)
{
    uint2 pixel = DdgiProbeStatePixel(probeIndex);
    Texture2D<float4> historyL0 = GetTexture(irradianceL0HistoryReadHandle);
    Texture2D<float4> historyLy = GetTexture(irradianceLyHistoryReadHandle);
    Texture2D<float4> historyLz = GetTexture(irradianceLzHistoryReadHandle);
    Texture2D<float4> historyLx = GetTexture(irradianceLxHistoryReadHandle);
    RWTexture2D<float4> outputL0 = GetRWTexture(irradianceL0HistoryWriteHandle);
    RWTexture2D<float4> outputLy = GetRWTexture(irradianceLyHistoryWriteHandle);
    RWTexture2D<float4> outputLz = GetRWTexture(irradianceLzHistoryWriteHandle);
    RWTexture2D<float4> outputLx = GetRWTexture(irradianceLxHistoryWriteHandle);
    outputL0[pixel] = historyL0.Load(int3(pixel, 0));
    outputLy[pixel] = historyLy.Load(int3(pixel, 0));
    outputLz[pixel] = historyLz.Load(int3(pixel, 0));
    outputLx[pixel] = historyLx.Load(int3(pixel, 0));
}

[numthreads(8, 8, 1)]
void DdgiIrradianceIntegrateCS(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID,
    uint groupIndex : SV_GroupIndex)
{
    uint probeIndex = groupId.x;
    if (probeIndex >= probeCount)
    {
        return;
    }

    if (!DdgiIsProbeUpdateActive(probeIndex))
    {
        if (groupIndex == 0u)
        {
            CopyHistory(probeIndex);
        }
        return;
    }

    uint rayCount = DdgiRaySampleCount(irradianceTileInteriorSize);
    if (groupIndex < rayCount)
    {
        uint2 samplePixel = DdgiRaySamplePixel(
            probeIndex,
            groupIndex,
            irradianceTileInteriorSize);
        g_TraceRadiance[groupIndex] =
            GetTexture(traceIrradianceReadHandle).Load(int3(samplePixel, 0)).rgb;
        g_TraceDirections[groupIndex] =
            DdgiRayDirection(groupIndex, rayCount, probeIndex);
    }
    GroupMemoryBarrierWithGroupSync();

    float2 octUv = ((float2(groupThreadId.xy) + 0.5f) / float(DDGI_DIRECTION_RESOLUTION)) * 2.0f - 1.0f;
    float3 direction = DdgiOctDecode(octUv);
    float3 currentRadiance = IntegrateDirection(direction, rayCount);
    uint estimatorIndex = DdgiPhysicalProbeIndex(probeIndex) * DDGI_DIRECTION_COUNT + groupIndex;
    DdgiVarianceData varianceData;
    if (!DdgiProbeHasUsableHistory(probeIndex))
    {
        varianceData = (DdgiVarianceData)0;
        varianceData.mean = currentRadiance;
        varianceData.shortMean = currentRadiance;
        varianceData.inconsistency = 1.0f;
    }
    else
    {
        varianceData = LoadVarianceData(estimatorIndex);
    }
    MultiscaleMeanEstimator(currentRadiance, varianceData);
    StoreVarianceData(estimatorIndex, varianceData);
    g_FilteredRadiance[groupIndex] = PackRgbe(varianceData.mean);

    GroupMemoryBarrierWithGroupSync();
    if (groupIndex != 0u)
    {
        return;
    }

    float3 coefficientL0 = 0.0f.xxx;
    float3 coefficientLy = 0.0f.xxx;
    float3 coefficientLz = 0.0f.xxx;
    float3 coefficientLx = 0.0f.xxx;
    float solidAngleWeightSum = 0.0f;
    for (uint sampleIndex = 0u; sampleIndex < DDGI_DIRECTION_COUNT; sampleIndex++)
    {
        uint2 sampleCoord = uint2(sampleIndex % DDGI_DIRECTION_RESOLUTION, sampleIndex / DDGI_DIRECTION_RESOLUTION);
        float2 sampleUv = ((float2(sampleCoord) + 0.5f) / float(DDGI_DIRECTION_RESOLUTION)) * 2.0f - 1.0f;
        float3 sampleDirection = DdgiOctDecode(sampleUv);
        float3 sampleRadiance = UnpackRgbe(g_FilteredRadiance[sampleIndex]);
        float3 unnormalizedDirection = float3(
            sampleUv.x,
            sampleUv.y,
            1.0f - abs(sampleUv.x) - abs(sampleUv.y));
        float solidAngleWeight = rcp(max(
            dot(unnormalizedDirection, unnormalizedDirection) *
            length(unnormalizedDirection),
            1e-5f));
        coefficientL0 += sampleRadiance * (DDGI_SH_BASIS_L0 * solidAngleWeight);
        coefficientLy += sampleRadiance * (DDGI_SH_BASIS_L1 * sampleDirection.y * solidAngleWeight);
        coefficientLz += sampleRadiance * (DDGI_SH_BASIS_L1 * sampleDirection.z * solidAngleWeight);
        coefficientLx += sampleRadiance * (DDGI_SH_BASIS_L1 * sampleDirection.x * solidAngleWeight);
        solidAngleWeightSum += solidAngleWeight;
    }

    float projectionScale = 4.0f * WOLF_PI / max(solidAngleWeightSum, 1e-5f);
    uint2 outputPixel = DdgiProbeStatePixel(probeIndex);
    RWTexture2D<float4> outputL0 = GetRWTexture(irradianceL0HistoryWriteHandle);
    RWTexture2D<float4> outputLy = GetRWTexture(irradianceLyHistoryWriteHandle);
    RWTexture2D<float4> outputLz = GetRWTexture(irradianceLzHistoryWriteHandle);
    RWTexture2D<float4> outputLx = GetRWTexture(irradianceLxHistoryWriteHandle);
    outputL0[outputPixel] = float4(coefficientL0 * projectionScale, 0.0f);
    outputLy[outputPixel] = float4(coefficientLy * projectionScale, 0.0f);
    outputLz[outputPixel] = float4(coefficientLz * projectionScale, 0.0f);
    outputLx[outputPixel] = float4(coefficientLx * projectionScale, 0.0f);
}
