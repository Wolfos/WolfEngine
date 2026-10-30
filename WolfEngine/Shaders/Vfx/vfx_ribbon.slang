// Draws a ribbon as one triangle strip: each point the CPU sampled contributes a root vertex and a tip vertex.
// Width, colour and opacity along the trail come from the emitter's baked curves.

#define WOLF_NO_RW_TEXTURES 1
import "../Common/common_bindless.slang";
import "vfx_common.slang";
import "vfx_shading.slang";

StructuredBuffer<GpuVfxEmitter> g_Emitters : register(t11);
StructuredBuffer<GpuVfxRibbonPoint> g_RibbonPoints : register(t12);

cbuffer VfxViewParams : register(b17)
{
    row_major float4x4 viewProjection;
    row_major float4x4 invProjection;
    float4 cameraOrigin;
    float4 cameraRight;
    float4 cameraUp;
    float4 cameraForward;
    uint depthHandle;
    uint3 _viewPad;
};

cbuffer VfxDrawParams : register(b18)
{
    uint emitterSlot;
    uint first;
    uint capacity;
    uint start;
    uint additive;
    uint3 _drawPad;
};

VfxVertexOutput vertexShader(uint vertexId : SV_VertexID)
{
    GpuVfxEmitter emitter = g_Emitters[emitterSlot];
    GpuVfxRibbonPoint point = g_RibbonPoints[first + (vertexId >> 1u)];
    bool tipSide = (vertexId & 1u) != 0u;
    float along = point.rootAlong.w;

    // Taper by scaling the root-to-tip edge about the pivot, so a sword trail can thin towards the blade's tip.
    float width = max(VfxSampleSizeCurve(emitter, along), 0.0f);
    float3 pivot = lerp(point.rootAlong.xyz, point.tipPad.xyz, emitter.render.w);
    float3 edge = tipSide ? point.tipPad.xyz : point.rootAlong.xyz;
    float3 position = pivot + (edge - pivot) * width;
    float3 relative = position + (emitter.origin.xyz - cameraOrigin.xyz);

    float4 color = VfxSampleColorCurve(emitter, along);
    color.rgb *= emitter.render.y;

    VfxVertexOutput output;
    output.position = mul(float4(relative, 1.0f), viewProjection);
    output.uv = float2(along, tipSide ? 1.0f : 0.0f);
    output.color = color;
    output.textureAndSampler = uint2(emitter.textureHandle, emitter.samplerHandle);
    output.depthAndSoftness = float2(output.position.w, emitter.render.z);
    return output;
}

float4 fragmentShader(VfxVertexOutput input) : SV_Target0
{
    return VfxShade(input, depthHandle, invProjection, additive != 0u, true);
}
