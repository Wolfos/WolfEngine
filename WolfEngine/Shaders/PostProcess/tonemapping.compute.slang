import "../Common/common_bindless.slang";
import "../ThirdParty/Aces/aces2_sdr_output.slang";

cbuffer BindlessHandles : register(b0)
{
    uint inputHandle;
    uint outputHandle;
    uint samplerHandle;
    uint lutHandle;
};

cbuffer TonemappingSettings : register(b1)
{
    uint renderSizeX;
    uint renderSizeY;
    float exposure;
    // Zero disables colour grading; lutHandle is then the invalid handle.
    float lutContribution;
    float3 lutDomainMin;
    // (N - 1) / N and 0.5 / N: map [0, 1] onto texel centres so the lattice ends are not filtered
    // against the clamped border.
    float lutScale;
    float3 lutInverseDomainExtent;
    float lutOffset;
};

bool TryLoadExposedSceneHdr(uint2 pixel, out float3 exposedSceneHdr)
{
    exposedSceneHdr = 0.0f.xxx;
    if (pixel.x >= renderSizeX || pixel.y >= renderSizeY)
    {
        return false;
    }

    Texture2D<float4> inputTexture = GetTexture(inputHandle);
    SamplerState linearSampler = GetSampler(samplerHandle);

    float2 renderSize = float2(renderSizeX, renderSizeY);
    float2 uv = (float2(pixel) + 0.5f) / renderSize;
    float3 sceneHdr = inputTexture.SampleLevel(linearSampler, uv, 0.0f).rgb;
    exposedSceneHdr = max(sceneHdr * exposure, 0.0f.xxx);
    return true;
}

float ColorGradingEncodeSrgbChannel(float linear)
{
    return linear <= 0.0031308f ? linear * 12.92f : 1.055f * pow(linear, 1.0f / 2.4f) - 0.055f;
}

float ColorGradingDecodeSrgbChannel(float encoded)
{
    return encoded <= 0.04045f ? encoded / 12.92f : pow((encoded + 0.055f) / 1.055f, 2.4f);
}

// .cube LUTs are authored against display-encoded values, so grading happens in sRGB encoding
// and the result is decoded back to the linear display space the rest of the chain expects.
float3 ApplyColorGrading(float3 linearDisplaySrgb)
{
    if (lutContribution <= 0.0f)
    {
        return linearDisplaySrgb;
    }

    float3 linearClamped = saturate(linearDisplaySrgb);
    float3 encoded = float3(
        ColorGradingEncodeSrgbChannel(linearClamped.r),
        ColorGradingEncodeSrgbChannel(linearClamped.g),
        ColorGradingEncodeSrgbChannel(linearClamped.b));
    float3 lattice = saturate((encoded - lutDomainMin) * lutInverseDomainExtent);
    float3 uvw = lattice * lutScale + lutOffset;

    Texture3D<float4> lut = GetTexture3D(lutHandle);
    float3 graded = lut.SampleLevel(GetSampler(samplerHandle), uvw, 0.0f).rgb;
    float3 blended = saturate(lerp(encoded, graded, lutContribution));
    return float3(
        ColorGradingDecodeSrgbChannel(blended.r),
        ColorGradingDecodeSrgbChannel(blended.g),
        ColorGradingDecodeSrgbChannel(blended.b));
}

void WriteLinearDisplaySrgb(uint2 pixel, float3 linearDisplaySrgb)
{
    RWTexture2D<float4> outputTexture = GetRWTexture(outputHandle);
    outputTexture[pixel] = float4(ApplyColorGrading(linearDisplaySrgb), 1.0f);
}

// ---------------------------------------------------------------------------------------------
// AgX (Troy Sobotka), base look without punchy/golden adjustments. Uses the 6th-order polynomial
// fit of the default contrast sigmoid and the inset/outset matrices from Blender/Filament.
// Matrices below are row-major and applied as mul(M, v).
// ---------------------------------------------------------------------------------------------

static const float3x3 kLinearSrgbToLinearRec2020 = float3x3(
    0.6274040f, 0.3292820f, 0.0433136f,
    0.0690970f, 0.9195400f, 0.0113612f,
    0.0163916f, 0.0880132f, 0.8955950f);

static const float3x3 kLinearRec2020ToLinearSrgb = float3x3(
     1.6604910f, -0.5876411f, -0.0728499f,
    -0.1245505f,  1.1328999f, -0.0083494f,
    -0.0181508f, -0.1005789f,  1.1187297f);

static const float3x3 kAgxInset = float3x3(
    0.856627153315983f, 0.0951212405381588f, 0.0482516061458583f,
    0.137318972929847f, 0.761241990602591f,  0.101439036467562f,
    0.11189821299995f,  0.0767994186031903f, 0.811302368396859f);

static const float3x3 kAgxOutset = float3x3(
     1.1271005818144368f,  -0.11060664309660323f, -0.016493938717834573f,
    -0.1413297634984383f,   1.157823702216272f,   -0.016493938717834257f,
    -0.14132976349843826f, -0.11060664309660294f,  1.2519364065950405f);

// log2(2^-10 * 0.18) and log2(2^6.5 * 0.18): the 16.5 stop window around middle grey.
static const float kAgxMinEv = -12.47393f;
static const float kAgxMaxEv = 4.026069f;

float3 AgxDefaultContrastApprox(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return 15.5f * x4 * x2
        - 40.14f * x4 * x
        + 31.96f * x4
        - 6.868f * x2 * x
        + 0.4298f * x2
        + 0.1191f * x
        - 0.00232f;
}

float3 AgxOutputTransform(float3 sceneLinearSrgb)
{
    float3 color = mul(kLinearSrgbToLinearRec2020, sceneLinearSrgb);
    color = mul(kAgxInset, color);

    color = log2(max(color, 1e-10f.xxx));
    color = saturate((color - kAgxMinEv) / (kAgxMaxEv - kAgxMinEv));
    color = AgxDefaultContrastApprox(color);

    color = mul(kAgxOutset, color);
    // The sigmoid produces a ~2.2 display encoding; undo it to stay in linear display space.
    color = pow(max(color, 0.0f.xxx), 2.2f.xxx);
    color = mul(kLinearRec2020ToLinearSrgb, color);
    return saturate(color);
}

// ---------------------------------------------------------------------------------------------
// Khronos PBR Neutral (https://github.com/KhronosGroup/ToneMapping). Keeps base colors intact up
// to ~0.8 and only compresses and desaturates highlights above that.
// ---------------------------------------------------------------------------------------------

float3 KhronosPbrNeutralOutputTransform(float3 sceneLinearSrgb)
{
    const float startCompression = 0.8f - 0.04f;
    const float desaturation = 0.15f;

    float3 color = sceneLinearSrgb;
    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08f ? x - 6.25f * x * x : 0.04f;
    color -= offset;

    float peak = max(color.r, max(color.g, color.b));
    if (peak < startCompression)
    {
        return color;
    }

    const float d = 1.0f - startCompression;
    float newPeak = 1.0f - d * d / (peak + d - startCompression);
    color *= newPeak / peak;

    float g = 1.0f - 1.0f / (desaturation * (peak - newPeak) + 1.0f);
    return lerp(color, newPeak.xxx, g);
}

[numthreads(8, 8, 1)]
void TonemappingAces(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    float3 exposedSceneHdr;
    if (TryLoadExposedSceneHdr(dispatchThreadId.xy, exposedSceneHdr) == false)
    {
        return;
    }

    WriteLinearDisplaySrgb(dispatchThreadId.xy, Aces2SdrOutputTransform(exposedSceneHdr));
}

[numthreads(8, 8, 1)]
void TonemappingAgX(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    float3 exposedSceneHdr;
    if (TryLoadExposedSceneHdr(dispatchThreadId.xy, exposedSceneHdr) == false)
    {
        return;
    }

    WriteLinearDisplaySrgb(dispatchThreadId.xy, AgxOutputTransform(exposedSceneHdr));
}

[numthreads(8, 8, 1)]
void TonemappingPbrNeutral(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    float3 exposedSceneHdr;
    if (TryLoadExposedSceneHdr(dispatchThreadId.xy, exposedSceneHdr) == false)
    {
        return;
    }

    WriteLinearDisplaySrgb(dispatchThreadId.xy, KhronosPbrNeutralOutputTransform(exposedSceneHdr));
}
