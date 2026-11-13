// The engine splash screen, drawn as one full-screen triangle into the display-encoded window image.
//
// The logo and text are signed distance fields in texels, negative inside (see SplashScreenContent). Coverage
// comes from the distance scaled to screen pixels, so the logo stays sharp at any window size. The logo's alpha
// channel says how far along the trace the nearest outline point is; the stroke is drawn wherever that is
// behind the trace's progress.
//
// Colours are display-encoded (sRGB) and blended as such, matching how the UI draws over the same image.
#define WOLF_NO_RW_TEXTURES 1
import "../Common/common_bindless.slang";

// Above the shared graphics registers, like the decal, outline and VFX passes.
cbuffer SplashParams : register(b17)
{
    // xy: top-left in pixels, zw: size in pixels.
    float4 logoRect;
    float4 textRect;
    // x: pixels per logo texel, y: pixels per text texel, z: stroke half width, w: glow radius (both in pixels).
    float4 distanceScale;
    float4 backgroundColor;
    float4 silhouetteColor;
    float4 highlightColor;
    // rgb: text colour, a: the "Powered by" line's strength relative to the wordmark.
    float4 textColor;
    // rgb: stroke colour, a: glow strength.
    float4 strokeColor;
    // x: trace progress, y: stroke opacity, z: fill opacity, w: length of the bright head of the trace.
    float4 animation;
    // x: "Powered by" opacity, y: wordmark opacity, z: opacity of the whole splash.
    float4 textAnimation;
    uint logoTextureHandle;
    uint textTextureHandle;
    uint samplerHandle;
    uint _handlePadding;
};

struct VSOutput
{
    float4 position : SV_Position;
};

VSOutput vertexShader(uint vertexId : SV_VertexID)
{
    // One triangle that covers the screen: (-1,-1), (3,-1), (-1,3).
    float2 corner = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u));
    VSOutput output;
    output.position = float4(corner * 2.0f - 1.0f, 0.0f, 1.0f);
    return output;
}

// Coverage of a pixel whose centre is this many pixels outside a shape's edge.
float Coverage(float distancePixels)
{
    return saturate(0.5f - distancePixels);
}

bool InsideRect(float2 uv)
{
    return all(uv >= 0.0f.xx) && all(uv <= 1.0f.xx);
}

float3 DrawLogo(float3 color, float2 pixel, SamplerState linearSampler)
{
    float2 uv = (pixel - logoRect.xy) / logoRect.zw;
    if (InsideRect(uv) == false)
    {
        return color;
    }

    float4 logo = GetTexture(logoTextureHandle).SampleLevel(linearSampler, uv, 0.0f);
    float pixelsPerTexel = distanceScale.x;
    float fill = animation.z;
    color = lerp(color, silhouetteColor.rgb, Coverage(logo.r * pixelsPerTexel) * fill);
    color = lerp(color, highlightColor.rgb, Coverage(logo.g * pixelsPerTexel) * fill);
    color = lerp(color, silhouetteColor.rgb, Coverage(logo.b * pixelsPerTexel) * fill);

    float strokeOpacity = animation.y;
    if (strokeOpacity <= 0.0f)
    {
        return color;
    }

    // Where the drawn part ends, softened over a short stretch of the outline so its tip is not aliased. The
    // progress overshoots by that stretch so the halves fully meet at the end.
    const float tipSoftness = 0.004f;
    float progress = animation.x;
    float along = logo.a;
    float drawn = saturate((progress * (1.0f + tipSoftness) - along) / tipSoftness);

    float edgePixels = abs(logo.r) * pixelsPerTexel;
    float halfWidth = distanceScale.z;
    float glowRadius = distanceScale.w;
    float core = saturate(halfWidth + 0.5f - edgePixels);
    float glow = pow(saturate(1.0f - edgePixels / glowRadius), 3.0f) * strokeColor.a;

    // A brighter, wider glow at the tip while the trace runs, gone once both halves have met.
    float tipDistance = (progress - along) / max(animation.w, 1e-4f);
    float tip = exp(-tipDistance * tipDistance) * (1.0f - smoothstep(0.85f, 1.0f, progress));
    float tipGlow = tip * pow(saturate(1.0f - edgePixels / (glowRadius * 1.6f)), 2.0f);

    float light = (core + glow + tipGlow) * drawn * strokeOpacity;
    return color + strokeColor.rgb * light;
}

float3 DrawText(float3 color, float2 pixel, SamplerState linearSampler)
{
    float2 uv = (pixel - textRect.xy) / textRect.zw;
    if (InsideRect(uv) == false)
    {
        return color;
    }

    float2 text = GetTexture(textTextureHandle).SampleLevel(linearSampler, uv, 0.0f).rg;
    float pixelsPerTexel = distanceScale.y;
    float poweredBy = Coverage(text.r * pixelsPerTexel) * textAnimation.x * textColor.a;
    float wordmark = Coverage(text.g * pixelsPerTexel) * textAnimation.y;
    color = lerp(color, textColor.rgb, poweredBy);
    color = lerp(color, textColor.rgb, wordmark);
    return color;
}

float4 fragmentShader(VSOutput input) : SV_Target
{
    float2 pixel = input.position.xy;
    SamplerState linearSampler = GetSampler(samplerHandle);

    float3 color = backgroundColor.rgb;
    color = DrawLogo(color, pixel, linearSampler);
    color = DrawText(color, pixel, linearSampler);

    return float4(saturate(color), textAnimation.z);
}
