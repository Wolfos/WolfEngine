import "../Common/common.slang";
import "../Common/common_bindless.slang";

// Spatiotemporal variance-guided filtering (Schied et al., HPG 2017) of the DDGI final gather, at trace
// resolution. The signal is already demodulated: the trace stores irradiance / PI, and deferred lighting
// multiplies the albedo back in. Three entry points run in order each frame:
//
//   DdgiSvgfTemporalCS  Reprojects last frame's history through the motion vectors with a 2x2 bilinear
//                       tap set, keeps only the taps on the same surface (falling back to 3x3 for thin
//                       geometry), and blends this frame's sample in with an exponential moving average.
//                       Luminance moments accumulate the same way.
//   DdgiSvgfVarianceCS  Turns the moments into a luminance variance. Until a pixel has four frames of
//                       history it estimates the variance spatially from a 7x7 bilateral neighbourhood.
//   DdgiSvgfFilterCS    One level of the edge-avoiding a-trous wavelet transform, with depth, normal and
//                       variance-normalised luminance edge stopping, also filtering the variance. Run once
//                       per level with the step size doubling. The first level's output is the color
//                       history the next frame's temporal pass reads.
//
// The edge-stopping parameters are the paper's; it reports they work across scenes and does not expose them.

cbuffer BindlessHandles : register(b0)
{
    uint depthHandle;
    uint normalHandle;
    uint velocityHandle;
    uint traceHandle;
    uint colorHistoryHandle;
    uint momentsHistoryHandle;
    uint depthHistoryHandle;
    uint normalHistoryHandle;
    uint integratedHandle;
    uint momentsHandle;
    uint guideDepthHandle;
    uint guideNormalHandle;
    uint sourceHandle;
    uint outputHandle;
};

cbuffer SvgfSettings : register(b1)
{
    row_major float4x4 invViewProjection;
    row_major float4x4 previousViewProjection;
    float3 cameraMotion;
    float _settingsPadding;
    uint framebufferSizeX;
    uint framebufferSizeY;
    uint traceSizeX;
    uint traceSizeY;
    float projZBias;
    float projZScale;
    uint historyValid;
    float colorAlpha;
    float momentsAlpha;
    uint stepSize;
};

static const float SIGMA_DEPTH = 1.0f;
static const float SIGMA_NORMAL = 128.0f;
static const float SIGMA_LUMINANCE = 4.0f;

/// History length below which the variance is estimated spatially rather than from the pixel's moments.
static const float SPATIAL_VARIANCE_HISTORY = 4.0f;
static const int SPATIAL_VARIANCE_RADIUS = 3;

/// History length is stored, not used beyond the moving-average floor, so it only needs to count past that.
static const float MAX_HISTORY_LENGTH = 64.0f;

/// Reprojection consistency: relative depth difference allowed on a surface facing the camera, widened by the
/// local depth slope, and the least normal agreement between a history tap and this frame's surface.
static const float REPROJECTION_DEPTH_TOLERANCE = 0.02f;
static const float REPROJECTION_MIN_NORMAL_DOT = 0.8f;

/// Depth edge stopping divides by the expected depth change along the offset; this floor, relative to the
/// pixel's depth, keeps surfaces facing the camera (zero slope) from rejecting their own depth noise.
static const float DEPTH_EPSILON_RELATIVE = 1.0e-3f;
static const float LUMINANCE_EPSILON = 1.0e-10f;

uint2 TraceSize()
{
    return uint2(traceSizeX, traceSizeY);
}

/// The full-resolution pixel a trace pixel samples. Must match the trace pass.
uint2 RepresentativePixel(uint2 tracePixel)
{
    uint2 fullSize = uint2(framebufferSizeX, framebufferSizeY);
    float2 fullPixelPosition = (float2(tracePixel) + 0.5f) * float2(fullSize) / float2(TraceSize());
    return min(uint2(fullPixelPosition), fullSize - 1u);
}

/// Positive view-space distance along the camera axis. projZBias and projZScale are the projection's M33 and M43.
float LinearDepth(float deviceDepth)
{
    float denominator = deviceDepth - projZBias;
    if (abs(denominator) < 1e-6f)
    {
        denominator = denominator < 0.0f ? -1e-6f : 1e-6f;
    }
    return abs(projZScale / denominator);
}

float3 DecodeGBufferNormal(float3 encoded)
{
    float3 normal = encoded * 2.0f - 1.0f;
    float lengthSquared = dot(normal, normal);
    return lengthSquared > 1e-8f ? normal * rsqrt(lengthSquared) : float3(0.0f, 1.0f, 0.0f);
}

/// False for NaN or infinity in any channel. Tests the exponent bits because fast-math compilation may fold
/// isfinite to true. One bad value would otherwise spread through every filter level and live on in history.
bool IsFinite(float4 value)
{
    uint4 exponent = asuint(value) & 0x7F800000u;
    return all(exponent != 0x7F800000u.xxxx);
}

float Luminance(float3 color)
{
    return dot(color, float3(0.2126f, 0.7152f, 0.0722f));
}

float2 OctEncode(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 encoded = n.xy;
    if (n.z < 0.0f)
    {
        float2 signs = float2(n.x >= 0.0f ? 1.0f : -1.0f, n.y >= 0.0f ? 1.0f : -1.0f);
        encoded = (1.0f - abs(n.yx)) * signs;
    }
    return encoded;
}

float3 OctDecode(float2 encoded)
{
    float3 n = float3(encoded, 1.0f - abs(encoded.x) - abs(encoded.y));
    float t = saturate(-n.z);
    n.x += n.x >= 0.0f ? -t : t;
    n.y += n.y >= 0.0f ? -t : t;
    return normalize(n);
}

/// Linear view depth the surface at a full-resolution pixel had last frame, assuming it did not move: its
/// position through last frame's camera. Left-handed perspective, so view depth is clip w. The g-buffer's
/// motion vectors carry a previous depth too, but only at half precision, which near 1 cannot tell surfaces
/// metres apart. cameraMotion is this frame's camera origin minus last frame's; the matrices are
/// camera-relative.
float PreviousLinearDepth(uint2 pixel, float deviceDepth)
{
    float2 uv = (float2(pixel) + 0.5f) / float2(framebufferSizeX, framebufferSizeY);
    float4 clip = float4(uv.x * 2.0f - 1.0f, -(uv.y * 2.0f - 1.0f), deviceDepth, 1.0f);
    float4 positionH = mul(clip, invViewProjection);
    float3 cameraRelative = positionH.xyz / positionH.w;
    float4 previousClip = mul(float4(cameraRelative + cameraMotion, 1.0f), previousViewProjection);
    return previousClip.w;
}

bool InsideTrace(int2 pixel)
{
    return all(pixel >= 0) && all(pixel < int2(TraceSize()));
}

/// Screen-space gradient of linear depth, per trace pixel. Of the forward and backward differences along each
/// axis the smaller one is taken, so a pixel on a silhouette measures its own surface, not the jump to the next.
/// Sky neighbours, which store zero depth, are skipped.
float2 DepthGradient(Texture2D<float4> guideDepth, int2 pixel, float depth)
{
    float2 gradient = 0.0f.xx;
    [unroll]
    for (int axis = 0; axis < 2; axis++)
    {
        int2 offset = axis == 0 ? int2(1, 0) : int2(0, 1);
        float best = 1e30f;
        [unroll]
        for (int side = -1; side <= 1; side += 2)
        {
            int2 neighbour = pixel + offset * side;
            if (InsideTrace(neighbour) == false)
            {
                continue;
            }

            float neighbourDepth = guideDepth.Load(int3(neighbour, 0)).r;
            if (neighbourDepth <= 0.0f)
            {
                continue;
            }

            float difference = abs(neighbourDepth - depth);
            best = min(best, difference);
        }
        gradient[axis] = best < 1e30f ? best : 0.0f;
    }
    return gradient;
}

/// Exponent of the depth edge-stopping weight (paper eq. 3): deviation from the plane the local depth gradient
/// predicts, in units of that predicted change.
float DepthWeightExponent(float centerDepth, float2 centerGradient, float sampleDepth, float2 offset)
{
    float expected = abs(dot(centerGradient, offset));
    return abs(centerDepth - sampleDepth) / (SIGMA_DEPTH * expected + centerDepth * DEPTH_EPSILON_RELATIVE);
}

float NormalWeight(float3 centerNormal, float3 sampleNormal)
{
    return pow(saturate(dot(centerNormal, sampleNormal)), SIGMA_NORMAL);
}

[numthreads(8, 8, 1)]
void DdgiSvgfTemporalCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 tracePixel = dispatchThreadId.xy;
    if (any(tracePixel >= TraceSize()))
    {
        return;
    }

    RWTexture2D<float4> integratedOutput = GetRWTexture(integratedHandle);
    RWTexture2D<float4> momentsOutput = GetRWTexture(momentsHandle);
    RWTexture2D<float4> guideDepthOutput = GetRWTexture(guideDepthHandle);
    RWTexture2D<float4> guideNormalOutput = GetRWTexture(guideNormalHandle);

    uint2 pixel = RepresentativePixel(tracePixel);
    Texture2D<float4> depthTexture = GetTexture(depthHandle);
    float deviceDepth = depthTexture.Load(int3(pixel, 0)).r;
    if (IsBackgroundDepth(deviceDepth))
    {
        integratedOutput[tracePixel] = 0.0f.xxxx;
        momentsOutput[tracePixel] = 0.0f.xxxx;
        guideDepthOutput[tracePixel] = 0.0f.xxxx;
        guideNormalOutput[tracePixel] = 0.0f.xxxx;
        return;
    }

    float depth = LinearDepth(deviceDepth);
    float3 normal = DecodeGBufferNormal(GetTexture(normalHandle).Load(int3(pixel, 0)).xyz);
    guideDepthOutput[tracePixel] = float4(depth, 0.0f, 0.0f, 0.0f);
    guideNormalOutput[tracePixel] = float4(OctEncode(normal), 0.0f, 0.0f);

    float4 traceSample = GetTexture(traceHandle).Load(int3(tracePixel, 0));
    float3 sampleColor = IsFinite(traceSample) ? max(traceSample.rgb, 0.0f.xxx) : 0.0f.xxx;
    float sampleLuminance = Luminance(sampleColor);

    // The depth slope at this pixel, from the neighbouring trace pixels' g-buffer depth, widens the depth
    // test on surfaces seen at a grazing angle, where a one-pixel reprojection error is a large depth change.
    float slope = 0.0f;
    [unroll]
    for (int axis = 0; axis < 2; axis++)
    {
        int2 offset = axis == 0 ? int2(1, 0) : int2(0, 1);
        float best = 1e30f;
        [unroll]
        for (int side = -1; side <= 1; side += 2)
        {
            int2 neighbour = int2(tracePixel) + offset * side;
            if (InsideTrace(neighbour) == false)
            {
                continue;
            }

            float neighbourDeviceDepth = depthTexture.Load(int3(RepresentativePixel(uint2(neighbour)), 0)).r;
            if (IsBackgroundDepth(neighbourDeviceDepth))
            {
                continue;
            }

            best = min(best, abs(LinearDepth(neighbourDeviceDepth) - depth));
        }
        slope += best < 1e30f ? best : 0.0f;
    }

    float3 historyColor = 0.0f.xxx;
    float2 historyMoments = 0.0f.xx;
    float historyLength = 0.0f;
    bool historyFound = false;
    float4 velocity = GetTexture(velocityHandle).Load(int3(pixel, 0));
    if (historyValid != 0u && velocity.w > 0.0f)
    {
        float2 fullSize = float2(framebufferSizeX, framebufferSizeY);
        float2 previousFullPosition = float2(pixel) + 0.5f + velocity.xy;
        float2 previousTracePosition = previousFullPosition * float2(TraceSize()) / fullSize - 0.5f;
        float expectedDepth = PreviousLinearDepth(pixel, deviceDepth);
        float depthTolerance = expectedDepth * REPROJECTION_DEPTH_TOLERANCE + slope * 2.0f;

        Texture2D<float4> colorHistory = GetTexture(colorHistoryHandle);
        Texture2D<float4> momentsHistory = GetTexture(momentsHistoryHandle);
        Texture2D<float4> depthHistory = GetTexture(depthHistoryHandle);
        Texture2D<float4> normalHistory = GetTexture(normalHistoryHandle);

        int2 baseTap = int2(floor(previousTracePosition));
        float2 fraction = previousTracePosition - float2(baseTap);
        float4 colorSum = 0.0f.xxxx;
        float4 momentsSum = 0.0f.xxxx;
        float weightSum = 0.0f;

        // 2x2 bilinear taps, each tested on its own; the weight of a rejected tap is redistributed over the rest.
        [unroll]
        for (int tapIndex = 0; tapIndex < 4; tapIndex++)
        {
            int2 tapOffset = int2(tapIndex & 1, tapIndex >> 1);
            int2 tap = baseTap + tapOffset;
            if (InsideTrace(tap) == false)
            {
                continue;
            }

            float tapDepth = depthHistory.Load(int3(tap, 0)).r;
            if (tapDepth <= 0.0f || abs(tapDepth - expectedDepth) > depthTolerance)
            {
                continue;
            }

            float3 tapNormal = OctDecode(normalHistory.Load(int3(tap, 0)).rg);
            if (dot(tapNormal, normal) < REPROJECTION_MIN_NORMAL_DOT)
            {
                continue;
            }

            float4 tapColor = colorHistory.Load(int3(tap, 0));
            float4 tapMoments = momentsHistory.Load(int3(tap, 0));
            if (IsFinite(tapColor) == false || IsFinite(tapMoments) == false)
            {
                continue;
            }

            float2 bilinear = lerp(1.0f.xx - fraction, fraction, float2(tapOffset));
            float weight = bilinear.x * bilinear.y;
            colorSum += tapColor * weight;
            momentsSum += tapMoments * weight;
            weightSum += weight;
        }

        // Nothing consistent in the bilinear footprint: try a 3x3 box around the nearest pixel, which finds
        // thin geometry such as foliage that the 2x2 set straddles.
        if (weightSum <= 1e-4f)
        {
            colorSum = 0.0f.xxxx;
            momentsSum = 0.0f.xxxx;
            weightSum = 0.0f;
            int2 nearest = int2(floor(previousTracePosition + 0.5f));
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    int2 tap = nearest + int2(x, y);
                    if (InsideTrace(tap) == false)
                    {
                        continue;
                    }

                    float tapDepth = depthHistory.Load(int3(tap, 0)).r;
                    if (tapDepth <= 0.0f || abs(tapDepth - expectedDepth) > depthTolerance)
                    {
                        continue;
                    }

                    float3 tapNormal = OctDecode(normalHistory.Load(int3(tap, 0)).rg);
                    if (dot(tapNormal, normal) < REPROJECTION_MIN_NORMAL_DOT)
                    {
                        continue;
                    }

                    float4 tapColor = colorHistory.Load(int3(tap, 0));
                    float4 tapMoments = momentsHistory.Load(int3(tap, 0));
                    if (IsFinite(tapColor) == false || IsFinite(tapMoments) == false)
                    {
                        continue;
                    }

                    colorSum += tapColor;
                    momentsSum += tapMoments;
                    weightSum += 1.0f;
                }
            }
        }

        if (weightSum > 1e-4f)
        {
            historyColor = colorSum.rgb / weightSum;
            historyMoments = momentsSum.rg / weightSum;
            historyLength = momentsSum.b / weightSum;
            historyFound = historyLength > 0.0f;
        }
    }

    // A disocclusion starts over from this frame's sample. Otherwise blend with the paper's exponential moving
    // average, using a plain running mean while the history is still shorter than the average's window so
    // the first frames are not dominated by the first sample.
    float newHistoryLength = historyFound ? min(historyLength + 1.0f, MAX_HISTORY_LENGTH) : 1.0f;
    float colorBlend = historyFound ? max(colorAlpha, 1.0f / newHistoryLength) : 1.0f;
    float momentsBlend = historyFound ? max(momentsAlpha, 1.0f / newHistoryLength) : 1.0f;
    float3 integratedColor = lerp(historyColor, sampleColor, colorBlend);
    float2 moments = lerp(historyMoments, float2(sampleLuminance, sampleLuminance * sampleLuminance), momentsBlend);

    integratedOutput[tracePixel] = float4(integratedColor, newHistoryLength);
    momentsOutput[tracePixel] = float4(moments, newHistoryLength, 0.0f);
}

[numthreads(8, 8, 1)]
void DdgiSvgfVarianceCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 tracePixel = dispatchThreadId.xy;
    if (any(tracePixel >= TraceSize()))
    {
        return;
    }

    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    Texture2D<float4> guideDepth = GetTexture(guideDepthHandle);
    float depth = guideDepth.Load(int3(tracePixel, 0)).r;
    if (depth <= 0.0f)
    {
        output[tracePixel] = 0.0f.xxxx;
        return;
    }

    Texture2D<float4> integrated = GetTexture(integratedHandle);
    Texture2D<float4> momentsTexture = GetTexture(momentsHandle);
    float4 center = integrated.Load(int3(tracePixel, 0));
    float4 centerMoments = momentsTexture.Load(int3(tracePixel, 0));
    float historyLength = centerMoments.b;

    float variance;
    if (historyLength >= SPATIAL_VARIANCE_HISTORY)
    {
        variance = centerMoments.g - centerMoments.r * centerMoments.r;
    }
    else
    {
        // Too few frames for the moments to mean anything: estimate the variance from the neighbourhood on the
        // same surface, weighted by depth and normal only.
        Texture2D<float4> guideNormal = GetTexture(guideNormalHandle);
        float3 normal = OctDecode(guideNormal.Load(int3(tracePixel, 0)).rg);
        float2 gradient = DepthGradient(guideDepth, int2(tracePixel), depth);
        float2 momentsSum = 0.0f.xx;
        float weightSum = 0.0f;
        for (int y = -SPATIAL_VARIANCE_RADIUS; y <= SPATIAL_VARIANCE_RADIUS; y++)
        {
            for (int x = -SPATIAL_VARIANCE_RADIUS; x <= SPATIAL_VARIANCE_RADIUS; x++)
            {
                int2 offset = int2(x, y);
                int2 neighbour = int2(tracePixel) + offset;
                if (InsideTrace(neighbour) == false)
                {
                    continue;
                }

                float neighbourDepth = guideDepth.Load(int3(neighbour, 0)).r;
                if (neighbourDepth <= 0.0f)
                {
                    continue;
                }

                float3 neighbourNormal = OctDecode(guideNormal.Load(int3(neighbour, 0)).rg);
                float weight =
                    exp(-DepthWeightExponent(depth, gradient, neighbourDepth, float2(offset))) *
                    NormalWeight(normal, neighbourNormal);
                momentsSum += momentsTexture.Load(int3(neighbour, 0)).rg * weight;
                weightSum += weight;
            }
        }
        momentsSum /= max(weightSum, 1e-6f);
        variance = momentsSum.y - momentsSum.x * momentsSum.x;
    }

    output[tracePixel] = float4(center.rgb, max(variance, 0.0f));
}

[numthreads(8, 8, 1)]
void DdgiSvgfFilterCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 tracePixel = dispatchThreadId.xy;
    if (any(tracePixel >= TraceSize()))
    {
        return;
    }

    RWTexture2D<float4> output = GetRWTexture(outputHandle);
    Texture2D<float4> guideDepth = GetTexture(guideDepthHandle);
    float depth = guideDepth.Load(int3(tracePixel, 0)).r;
    if (depth <= 0.0f)
    {
        output[tracePixel] = 0.0f.xxxx;
        return;
    }

    Texture2D<float4> source = GetTexture(sourceHandle);
    Texture2D<float4> guideNormal = GetTexture(guideNormalHandle);
    float4 center = source.Load(int3(tracePixel, 0));
    float3 normal = OctDecode(guideNormal.Load(int3(tracePixel, 0)).rg);
    float2 gradient = DepthGradient(guideDepth, int2(tracePixel), depth);
    float centerLuminance = Luminance(center.rgb);

    // The luminance edge stopping is normalised by the local standard deviation. Its variance is prefiltered
    // with a 3x3 Gaussian first, because a single pixel's estimate is unstable at low sample counts. The
    // prefiltered value only steers the weights; the variance passed on is filtered below.
    static const float gaussian3x3[2] = { 0.25f, 0.125f };
    float prefilteredVariance = 0.0f;
    float prefilterWeightSum = 0.0f;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            int2 neighbour = int2(tracePixel) + int2(x, y);
            if (InsideTrace(neighbour) == false || guideDepth.Load(int3(neighbour, 0)).r <= 0.0f)
            {
                continue;
            }

            float weight = gaussian3x3[abs(x)] * gaussian3x3[abs(y)];
            prefilteredVariance += source.Load(int3(neighbour, 0)).a * weight;
            prefilterWeightSum += weight;
        }
    }
    prefilteredVariance /= max(prefilterWeightSum, 1e-6f);
    float luminanceScale = SIGMA_LUMINANCE * sqrt(max(prefilteredVariance, 0.0f)) + LUMINANCE_EPSILON;

    // 5x5 B3-spline kernel (1/16, 1/4, 3/8, 1/4, 1/16) with stepSize - 1 holes between taps (paper eq. 1).
    static const float kernel[3] = { 3.0f / 8.0f, 1.0f / 4.0f, 1.0f / 16.0f };
    float centerWeight = kernel[0] * kernel[0];
    float3 colorSum = center.rgb * centerWeight;
    float varianceSum = center.a * centerWeight * centerWeight;
    float weightSum = centerWeight;
    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            if (x == 0 && y == 0)
            {
                continue;
            }

            int2 offset = int2(x, y) * int(stepSize);
            int2 neighbour = int2(tracePixel) + offset;
            if (InsideTrace(neighbour) == false)
            {
                continue;
            }

            float neighbourDepth = guideDepth.Load(int3(neighbour, 0)).r;
            if (neighbourDepth <= 0.0f)
            {
                continue;
            }

            float4 neighbourValue = source.Load(int3(neighbour, 0));
            if (IsFinite(neighbourValue) == false)
            {
                continue;
            }

            float3 neighbourNormal = OctDecode(guideNormal.Load(int3(neighbour, 0)).rg);
            float luminanceExponent = abs(centerLuminance - Luminance(neighbourValue.rgb)) / luminanceScale;
            float edgeWeight =
                exp(-DepthWeightExponent(depth, gradient, neighbourDepth, float2(offset)) - luminanceExponent) *
                NormalWeight(normal, neighbourNormal);
            float weight = kernel[abs(x)] * kernel[abs(y)] * edgeWeight;
            colorSum += neighbourValue.rgb * weight;
            varianceSum += neighbourValue.a * weight * weight;
            weightSum += weight;
        }
    }

    // Variance of a weighted mean of uncorrelated samples.
    output[tracePixel] = float4(colorSum / weightSum, varianceSum / (weightSum * weightSum));
}
