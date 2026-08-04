import "../Common/common_bindless.slang";

cbuffer BindlessHandles : register(b0)
{
    uint currentColorHandle;
    uint velocityHandle;
    uint normalHandle;
    uint materialHandle;
    uint currentDepthHandle;
    uint historyColorHandle;
    uint historyDepthHandle;
    uint outputHandle;
    uint samplerHandle;
};

cbuffer TaaSettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    uint historyValid;
    uint resetHistory;
    float2 currentJitterPixels;
    float2 previousJitterPixels;
    float staticHistoryWeight;
    float movingHistoryWeight;
    float motionResponsePixels;
    float depthRejectionAbsolute;
    float depthRejectionRelative;
    float varianceClipGamma;
    float alphaTestHistoryScale;
    float currentProjectionZBias;
    float currentProjectionZScale;
    float previousProjectionZBias;
    float previousProjectionZScale;
    float staticClampExpansion;
    float clampExpansionMotionPixels;
    row_major float4x4 inverseUnjitteredViewProjection;
    row_major float4x4 previousViewProjection;
};

static const uint TAA_GROUP_WIDTH = 8u;
static const uint TAA_TILE_WIDTH = 10u;
static const uint TAA_TILE_PIXEL_COUNT = TAA_TILE_WIDTH * TAA_TILE_WIDTH;
static const float TAA_BACKGROUND_DEPTH = 0.999999f;
static const float TAA_MAX_HISTORY_AGE = 32.0f;
// Frames of accumulation before a stationary pixel is trusted enough to widen its
// neighbourhood clamp. Short enough to converge quickly, long enough that a freshly
// disoccluded pixel resolves against the tight box first.
static const float TAA_CLAMP_EXPANSION_AGE = 8.0f;

groupshared float4 g_CurrentColorTile[TAA_TILE_PIXEL_COUNT];

float3 RGBToYCoCg(float3 color)
{
    float y = dot(color, float3(0.25f, 0.5f, 0.25f));
    float co = color.r - color.b;
    float cg = color.g - 0.5f * (color.r + color.b);
    return float3(y, co, cg);
}

float3 YCoCgToRGB(float3 color)
{
    float y = color.x;
    float co = color.y;
    float cg = color.z;
    return float3(
        y + 0.5f * co - 0.5f * cg,
        y + 0.5f * cg,
        y - 0.5f * co - 0.5f * cg);
}

float MaxComponent(float3 value)
{
    return max(value.x, max(value.y, value.z));
}

bool IsBackground(float depth)
{
    return depth >= TAA_BACKGROUND_DEPTH;
}

float ReconstructViewDepth(float depth, float projectionZScale, float projectionZBias)
{
    float denominator = depth - projectionZBias;
    if (abs(denominator) < 1e-6f)
    {
        denominator = denominator < 0.0f ? -1e-6f : 1e-6f;
    }

    return projectionZScale / denominator;
}

float DepthThreshold(float expectedViewDepth)
{
    return max(
        depthRejectionAbsolute,
        abs(expectedViewDepth) * depthRejectionRelative);
}

float3 DecodeNormal(float3 encodedNormal)
{
    return normalize(encodedNormal * 2.0f - 1.0f);
}

bool IsNeighborhoodSampleCompatible(
    float centerDepth,
    float3 centerNormal,
    float sampleDepth,
    float3 sampleNormal)
{
    bool centerBackground = IsBackground(centerDepth);
    bool sampleBackground = IsBackground(sampleDepth);
    if (centerBackground || sampleBackground)
    {
        return centerBackground && sampleBackground;
    }

    float centerViewDepth = ReconstructViewDepth(
        centerDepth,
        currentProjectionZScale,
        currentProjectionZBias);
    float sampleViewDepth = ReconstructViewDepth(
        sampleDepth,
        currentProjectionZScale,
        currentProjectionZBias);
    return abs(sampleViewDepth - centerViewDepth) <= DepthThreshold(centerViewDepth) &&
        dot(centerNormal, sampleNormal) >= 0.8f;
}

float4 SampleCatmullRom(
    Texture2D<float4> texture,
    SamplerState samplerState,
    float2 uv,
    float2 textureSize)
{
    float2 samplePos = uv * textureSize;
    float2 texPos1 = floor(samplePos - 0.5f) + 0.5f;
    float2 f = samplePos - texPos1;

    float2 w0 = f * (-0.5f + f * (1.0f - 0.5f * f));
    float2 w1 = 1.0f + f * f * (-2.5f + 1.5f * f);
    float2 w2 = f * (0.5f + f * (2.0f - 1.5f * f));
    float2 w3 = f * f * (-0.5f + 0.5f * f);

    float2 w12 = w1 + w2;
    float2 offset12 = w2 / max(w12, 1e-5f.xx);

    float2 texPos0 = (texPos1 - 1.0f) / textureSize;
    float2 texPos3 = (texPos1 + 2.0f) / textureSize;
    float2 texPos12 = (texPos1 + offset12) / textureSize;

    return texture.SampleLevel(samplerState, float2(texPos0.x, texPos0.y), 0.0f) * w0.x * w0.y +
        texture.SampleLevel(samplerState, float2(texPos12.x, texPos0.y), 0.0f) * w12.x * w0.y +
        texture.SampleLevel(samplerState, float2(texPos3.x, texPos0.y), 0.0f) * w3.x * w0.y +
        texture.SampleLevel(samplerState, float2(texPos0.x, texPos12.y), 0.0f) * w0.x * w12.y +
        texture.SampleLevel(samplerState, float2(texPos12.x, texPos12.y), 0.0f) * w12.x * w12.y +
        texture.SampleLevel(samplerState, float2(texPos3.x, texPos12.y), 0.0f) * w3.x * w12.y +
        texture.SampleLevel(samplerState, float2(texPos0.x, texPos3.y), 0.0f) * w0.x * w3.y +
        texture.SampleLevel(samplerState, float2(texPos12.x, texPos3.y), 0.0f) * w12.x * w3.y +
        texture.SampleLevel(samplerState, float2(texPos3.x, texPos3.y), 0.0f) * w3.x * w3.y;
}

float3 ClipLineToAabb(
    float3 current,
    float3 history,
    float3 boxMin,
    float3 boxMax)
{
    float3 direction = history - current;
    float t = 1.0f;

    if (history.x < boxMin.x && direction.x < 0.0f)
        t = min(t, (boxMin.x - current.x) / direction.x);
    else if (history.x > boxMax.x && direction.x > 0.0f)
        t = min(t, (boxMax.x - current.x) / direction.x);

    if (history.y < boxMin.y && direction.y < 0.0f)
        t = min(t, (boxMin.y - current.y) / direction.y);
    else if (history.y > boxMax.y && direction.y > 0.0f)
        t = min(t, (boxMax.y - current.y) / direction.y);

    if (history.z < boxMin.z && direction.z < 0.0f)
        t = min(t, (boxMin.z - current.z) / direction.z);
    else if (history.z > boxMax.z && direction.z > 0.0f)
        t = min(t, (boxMax.z - current.z) / direction.z);

    return current + direction * saturate(t);
}

bool TryReprojectBackground(float2 currentUv, out float2 previousUv)
{
    float2 ndc = float2(
        currentUv.x * 2.0f - 1.0f,
        1.0f - currentUv.y * 2.0f);
    float4 worldPositionH = mul(
        float4(ndc, TAA_BACKGROUND_DEPTH, 1.0f),
        inverseUnjitteredViewProjection);
    if (abs(worldPositionH.w) < 1e-6f)
    {
        previousUv = 0.0f.xx;
        return false;
    }

    float3 worldDirection = normalize(worldPositionH.xyz / worldPositionH.w);
    float4 previousClip = mul(float4(worldDirection, 0.0f), previousViewProjection);
    if (previousClip.w <= 1e-6f)
    {
        previousUv = 0.0f.xx;
        return false;
    }

    float2 previousNdc = previousClip.xy / previousClip.w;
    previousUv = float2(
        previousNdc.x * 0.5f + 0.5f,
        -previousNdc.y * 0.5f + 0.5f);
    return true;
}

[numthreads(8, 8, 1)]
void TaaResolveCS(
    uint3 dispatchThreadId : SV_DispatchThreadID,
    uint3 groupThreadId : SV_GroupThreadID,
    uint3 groupId : SV_GroupID)
{
    Texture2D<float4> currentColorTexture = GetTexture(currentColorHandle);
    Texture2D<float4> velocityTexture = GetTexture(velocityHandle);
    Texture2D<float4> normalTexture = GetTexture(normalHandle);
    Texture2D<float4> materialTexture = GetTexture(materialHandle);
    Texture2D<float4> currentDepthTexture = GetTexture(currentDepthHandle);
    Texture2D<float4> historyColorTexture = GetTexture(historyColorHandle);
    Texture2D<float4> historyDepthTexture = GetTexture(historyDepthHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    uint laneIndex = groupThreadId.y * TAA_GROUP_WIDTH + groupThreadId.x;
    int2 maxPixel = int2(int(renderSizeX) - 1, int(renderSizeY) - 1);
    float2 renderSize = float2(renderSizeX, renderSizeY);
    int2 groupBasePixel = int2(groupId.xy * TAA_GROUP_WIDTH);

    for (uint tileIndex = laneIndex; tileIndex < TAA_TILE_PIXEL_COUNT; tileIndex += 64u)
    {
        int2 tilePixel = int2(
            int(tileIndex % TAA_TILE_WIDTH),
            int(tileIndex / TAA_TILE_WIDTH));
        int2 sourcePixel = clamp(
            groupBasePixel + tilePixel - int2(1, 1),
            int2(0, 0),
            maxPixel);
        float2 sourceUv =
            (float2(sourcePixel) + 0.5f.xx + currentJitterPixels) / renderSize;
        g_CurrentColorTile[tileIndex] =
            currentColorTexture.SampleLevel(linearSampler, sourceUv, 0.0f);
    }

    GroupMemoryBarrierWithGroupSync();

    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    int2 pixel = int2(dispatchThreadId.xy);
    float2 centerUv = (float2(pixel) + 0.5f.xx) / renderSize;
    uint centerTileIndex =
        (groupThreadId.y + 1u) * TAA_TILE_WIDTH + groupThreadId.x + 1u;
    float3 currentColor = g_CurrentColorTile[centerTileIndex].rgb;
    float3 currentYCoCg = RGBToYCoCg(currentColor);
    float centerDepth = currentDepthTexture.Load(int3(pixel, 0)).r;
    float3 centerNormal = DecodeNormal(normalTexture.Load(int3(pixel, 0)).xyz);
    bool background = IsBackground(centerDepth);
    float4 velocity = velocityTexture.Load(int3(pixel, 0));
    float motionPixels = background ? 0.0f : length(velocity.xy);
    float motionFactor = saturate(
        (motionPixels - 0.5f) / max(motionResponsePixels - 0.5f, 1e-4f));

    // Resolve the reprojection target up front. The clamp relaxation below has to know
    // how far this pixel actually travelled on screen, and for background pixels that
    // only falls out of the sky reprojection rather than the velocity buffer.
    bool historyAllowed = historyValid != 0u && resetHistory == 0u;
    float2 previousUv = centerUv;

    if (historyAllowed)
    {
        if (background)
        {
            historyAllowed = TryReprojectBackground(centerUv, previousUv);
        }
        else
        {
            historyAllowed = velocity.w > 0.0f;
            previousUv = centerUv + velocity.xy / renderSize;
        }
    }

    historyAllowed = historyAllowed &&
        all(previousUv >= 0.0f.xx) &&
        all(previousUv <= 1.0f.xx);

    // Screen-space travel including the background reprojection, so a rotating camera
    // does not read as stationary over the sky.
    float reprojectedMotionPixels = historyAllowed
        ? length((previousUv - centerUv) * renderSize)
        : 0.0f;
    float stationaryFactor = 1.0f - saturate(
        reprojectedMotionPixels / max(clampExpansionMotionPixels, 1e-4f));

    float3 neighborhoodMin = currentYCoCg;
    float3 neighborhoodMax = currentYCoCg;
    float3 firstMoment = 0.0f.xxx;
    float3 secondMoment = 0.0f.xxx;
    float sampleCount = 0.0f;

    for (int offsetY = -1; offsetY <= 1; offsetY++)
    {
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            int2 samplePixel = clamp(
                pixel + int2(offsetX, offsetY),
                int2(0, 0),
                maxPixel);
            float sampleDepth = currentDepthTexture.Load(int3(samplePixel, 0)).r;
            float3 sampleNormal = DecodeNormal(
                normalTexture.Load(int3(samplePixel, 0)).xyz);
            uint tileIndex =
                uint(int(groupThreadId.y) + offsetY + 1) * TAA_TILE_WIDTH +
                uint(int(groupThreadId.x) + offsetX + 1);
            float3 sampleYCoCg = RGBToYCoCg(g_CurrentColorTile[tileIndex].rgb);
            neighborhoodMin = min(neighborhoodMin, sampleYCoCg);
            neighborhoodMax = max(neighborhoodMax, sampleYCoCg);
            float sampleWeight = IsNeighborhoodSampleCompatible(
                centerDepth,
                centerNormal,
                sampleDepth,
                sampleNormal)
                ? 1.0f
                : 0.5f;
            firstMoment += sampleYCoCg * sampleWeight;
            secondMoment += sampleYCoCg * sampleYCoCg * sampleWeight;
            sampleCount += sampleWeight;
        }
    }

    float inverseSampleCount = rcp(max(sampleCount, 1.0f));
    float3 mean = firstMoment * inverseSampleCount;
    float3 variance = max(
        secondMoment * inverseSampleCount - mean * mean,
        0.0f.xxx);
    float3 sigma = sqrt(variance);
    float3 varianceMin = mean - varianceClipGamma * sigma;
    float3 varianceMax = mean + varianceClipGamma * sigma;
    float3 varianceBoxMin = max(neighborhoodMin, varianceMin);
    float3 varianceBoxMax = min(neighborhoodMax, varianceMax);
    float varianceInfluence = lerp(0.1f, 1.0f, motionFactor);
    float3 clipMin = lerp(
        neighborhoodMin,
        varianceBoxMin,
        varianceInfluence);
    float3 clipMax = lerp(
        neighborhoodMax,
        varianceBoxMax,
        varianceInfluence);

    float4 historySample = float4(currentColor, 0.0f);
    if (historyAllowed)
    {
        float2 historyDepthUv =
            previousUv + previousJitterPixels / renderSize;
        historyAllowed =
            all(historyDepthUv >= 0.0f.xx) &&
            all(historyDepthUv <= 1.0f.xx);
        float2 historyPixelPosition =
            historyDepthUv * renderSize - 0.5f.xx;
        int2 historyBasePixel = int2(floor(historyPixelPosition));
        uint matchingTapCount = 0u;
        uint matchingFootprintTapCount = 0u;
        float expectedPreviousViewDepth = background
            ? 0.0f
            : ReconstructViewDepth(
                velocity.z,
                previousProjectionZScale,
                previousProjectionZBias);
        float rejectionThreshold = DepthThreshold(expectedPreviousViewDepth);

        // Between two frames the jitter moves the raster grid by up to a pixel, so the
        // surface this pixel covers can land a whole pixel away in the history depth
        // buffer even when nothing moved. On sub-pixel geometry - cables, railings,
        // foliage - the bilinear 2x2 footprint then finds no matching depth, and the
        // resulting rejection resets the accumulation and puts a raw aliased frame back
        // on screen. Give the search exactly that one pixel of raster slack while the
        // pixel is stationary. A genuine disocclusion still fails every tap, and anything
        // actually moving keeps the plain 2x2 footprint, so rejection under motion is
        // unchanged.
        int depthSearchSlack = stationaryFactor > 0.75f ? 1 : 0;

        for (int tapY = -depthSearchSlack; tapY <= 1 + depthSearchSlack; tapY++)
        {
            for (int tapX = -depthSearchSlack; tapX <= 1 + depthSearchSlack; tapX++)
            {
                int2 historyPixel = historyBasePixel + int2(tapX, tapY);
                bool tapInBounds =
                    historyPixel.x >= 0 &&
                    historyPixel.y >= 0 &&
                    historyPixel.x < int(renderSizeX) &&
                    historyPixel.y < int(renderSizeY);
                if (!tapInBounds)
                {
                    continue;
                }

                float historyDepth =
                    historyDepthTexture.Load(int3(historyPixel, 0)).r;
                bool tapMatches = false;
                float depthError = 0.0f;
                if (background)
                {
                    tapMatches = IsBackground(historyDepth);
                }
                else if (!IsBackground(historyDepth))
                {
                    float historyViewDepth = ReconstructViewDepth(
                        historyDepth,
                        previousProjectionZScale,
                        previousProjectionZBias);
                    depthError = abs(historyViewDepth - expectedPreviousViewDepth);
                    tapMatches = depthError <= rejectionThreshold;
                }

                if (tapMatches)
                {
                    matchingTapCount++;
                    // Only the bilinear 2x2 decides whether the wider Catmull-Rom
                    // history filter is safe; the slack ring validates the surface but
                    // says nothing about the taps that filter would read.
                    if (tapX >= 0 && tapX <= 1 && tapY >= 0 && tapY <= 1)
                    {
                        matchingFootprintTapCount++;
                    }
                }
            }
        }

        historyAllowed = historyAllowed && matchingTapCount > 0u;
        if (historyAllowed && matchingFootprintTapCount == 4u)
        {
            historySample = SampleCatmullRom(
                historyColorTexture,
                linearSampler,
                previousUv,
                renderSize);
        }
        else if (historyAllowed)
        {
            // Depth history is jittered G-buffer data, while color history is already
            // reconstructed into unjittered output space. The matching depth taps
            // validate the surface, but the color point sample must stay at prevUv.
            int2 historyColorPixel = clamp(
                int2(previousUv * renderSize),
                int2(0, 0),
                maxPixel);
            historySample = historyColorTexture.Load(int3(historyColorPixel, 0));
        }
    }

    float3 resolvedColor = currentColor;
    float outputHistoryAge = 0.0f;
    if (historyAllowed)
    {
        float previousHistoryAge =
            clamp(historySample.a, 0.0f, TAA_MAX_HISTORY_AGE);

        // A converged stationary pixel's history is the supersampled average of this
        // surface point across every jitter phase. One phase's 3x3 neighbourhood is a
        // far narrower range than that average legitimately spans on high-frequency
        // geometry and foliage, so clamping to it hands the aliasing straight back each
        // frame. Widen the box in proportion to local contrast once the pixel has both
        // stopped moving and had time to converge - moving and freshly disoccluded
        // pixels keep the tight box that rejects ghosting and disocclusion leaks.
        float convergedFactor =
            saturate(previousHistoryAge / TAA_CLAMP_EXPANSION_AGE);
        float clampExpansion = lerp(
            1.0f,
            max(staticClampExpansion, 1.0f),
            stationaryFactor * convergedFactor);
        // Applied as an offset rather than rebuilt from the box centre so that an
        // expansion of exactly 1.0 leaves the moving-pixel clamp bit-identical.
        float3 expansionOffset =
            0.5f * (clipMax - clipMin) * (clampExpansion - 1.0f);
        float3 expandedClipMin = min(clipMin - expansionOffset, currentYCoCg);
        float3 expandedClipMax = max(clipMax + expansionOffset, currentYCoCg);

        float3 historyYCoCg = RGBToYCoCg(historySample.rgb);
        float3 clippedHistoryYCoCg = ClipLineToAabb(
            currentYCoCg,
            historyYCoCg,
            expandedClipMin,
            expandedClipMax);
        float3 clipRange = max(
            expandedClipMax - expandedClipMin,
            float3(0.02f, 0.02f, 0.02f));
        float clipDistance = MaxComponent(
            abs(historyYCoCg - clippedHistoryYCoCg) / clipRange);
        float clipResponse = saturate(clipDistance);

        float luminanceDenominator = max(
            max(abs(currentYCoCg.x), abs(historyYCoCg.x)),
            0.1f);
        float relativeLuminanceDifference =
            abs(historyYCoCg.x - currentYCoCg.x) / luminanceDenominator;
        float luminanceResponse =
            smoothstep(0.1f, 0.5f, relativeLuminanceDifference);
        // On stationary geometry these two responses are driven by the frame's own
        // aliasing rather than by stale history, so letting them cut the weight there
        // feeds the flicker it is meant to suppress.
        float confidencePenaltyScale = 1.0f - stationaryFactor * convergedFactor;
        float historyConfidence = 1.0f - confidencePenaltyScale *
            (0.025f * clipResponse + 0.025f * luminanceResponse);

        float historyWeightCap = lerp(
            staticHistoryWeight,
            movingHistoryWeight,
            motionFactor);
        float alphaTestMask =
            saturate(materialTexture.Load(int3(pixel, 0)).a);
        // Alpha-tested coverage swims between phases, so its history is discounted while
        // it moves. A stationary alpha-tested pixel has no such problem, and it is the
        // sub-pixel foliage that most needs a long accumulation to converge - keeping the
        // discount there is what leaves foliage visibly boiling on a locked camera.
        float alphaTestScale = lerp(
            alphaTestHistoryScale,
            1.0f,
            stationaryFactor);
        historyWeightCap *= lerp(
            1.0f,
            alphaTestScale,
            alphaTestMask);

        float startupWeight =
            previousHistoryAge / (previousHistoryAge + 1.0f);
        float historyWeight =
            min(startupWeight, historyWeightCap) * historyConfidence;
        resolvedColor = lerp(
            currentColor,
            YCoCgToRGB(clippedHistoryYCoCg),
            historyWeight);
        outputHistoryAge = min(
            previousHistoryAge + 1.0f,
            TAA_MAX_HISTORY_AGE);
    }

    outputTexture[pixel] = float4(max(resolvedColor, 0.0f.xxx), outputHistoryAge);
}
