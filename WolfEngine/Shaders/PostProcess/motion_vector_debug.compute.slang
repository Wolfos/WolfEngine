import "../Common/common_bindless.slang";

// Flow-field visualization for the G-buffer velocity target.
//
// The velocity target stores `previousPixel - currentPixel` in pixels (a backward vector, which
// is what the temporal passes want for history fetches). Handing that texture straight to the
// viewport clamps every channel to [0, 1], so anything past a single pixel of motion saturates
// and all you can read is the sign of each axis. Here direction becomes hue and magnitude
// becomes brightness, so a forward camera dolly reads as the radial expansion it actually is.

cbuffer BindlessHandles : register(b0)
{
    uint velocityHandle;
    uint outputHandle;
};

cbuffer MotionVectorDebugSettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    float maxPixelsPerFrame;  // Displacement that maps to full brightness.
    uint legendRadiusPixels;  // 0 disables the legend wheel.
};

static const float Pi = 3.14159265f;
static const float LegendMarginPixels = 12.0f;

float3 HueToRgb(float hue01)
{
    return saturate(float3(
        abs(hue01 * 6.0f - 3.0f) - 1.0f,
        2.0f - abs(hue01 * 6.0f - 2.0f),
        2.0f - abs(hue01 * 6.0f - 4.0f)));
}

float3 FlowToRgb(float2 flowPixels, float maxPixels)
{
    // Screen-space y grows downward, so flip it and "up on screen" becomes up in the wheel.
    float hue = atan2(-flowPixels.y, flowPixels.x) / (2.0f * Pi);
    hue = hue - floor(hue);  // atan2 returns [-pi, pi]; wrap the hue into [0, 1).

    // Square-root compression keeps sub-pixel motion visible without flattening everything
    // fast into the same colour the way a linear ramp does.
    float value = sqrt(saturate(length(flowPixels) / max(maxPixels, 1e-4f)));
    return HueToRgb(hue) * value;
}

float3 LinearToSrgb(float3 color)
{
    float3 cutoff = step(color, 0.0031308f.xxx);
    float3 lower = color * 12.92f;
    float3 upper = 1.055f * pow(color, 1.0f / 2.4f) - 0.055f;
    return lerp(upper, lower, cutoff);
}

[numthreads(8, 8, 1)]
void MotionVectorDebugCS(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= renderSizeX || dispatchThreadId.y >= renderSizeY)
    {
        return;
    }

    Texture2D<float4> velocityTexture = GetTexture(velocityHandle);
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    int2 pixel = int2(dispatchThreadId.xy);

    // The stored vector points back at the previous frame; negate it so hue follows the
    // direction the surface is actually travelling across the screen.
    float2 flow = -velocityTexture.Load(int3(pixel, 0)).xy;
    float3 color = FlowToRgb(flow, maxPixelsPerFrame);

    if (legendRadiusPixels > 0)
    {
        float radius = float(legendRadiusPixels);
        float2 center = float2(
            float(renderSizeX) - radius - LegendMarginPixels,
            float(renderSizeY) - radius - LegendMarginPixels);
        float2 offset = float2(pixel) - center;
        float distanceFromCenter = length(offset);
        if (distanceFromCenter <= radius)
        {
            // The legend runs the same mapping over its own offset-from-centre, so a pixel's
            // colour can be matched against the spoke of the wheel that produced it.
            float3 legend = FlowToRgb(offset / radius * maxPixelsPerFrame, maxPixelsPerFrame);
            color = lerp(color, legend, saturate(radius - distanceFromCenter));
        }
    }

    outputTexture[pixel] = float4(LinearToSrgb(saturate(color)), 1.0f);
}
