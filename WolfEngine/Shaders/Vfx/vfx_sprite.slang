// Draws one sprite emitter's ring of particles as camera-facing, ground-aligned or velocity-stretched quads.
//
// One instance per ring slot, in ring order from the oldest particle, so alpha-blended particles of one emitter
// always draw in the same order and never flicker. Dead slots collapse to a clipped point.

#define WOLF_NO_RW_TEXTURES 1
import "../Common/common_bindless.slang";
import "vfx_common.slang";
import "vfx_shading.slang";

StructuredBuffer<GpuVfxParticle> g_Particles : register(t10);
StructuredBuffer<GpuVfxEmitter> g_Emitters : register(t11);

// Above the shared graphics registers, like the decal pass, so nothing else in the frame collides with them.
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

VfxVertexOutput vertexShader(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    GpuVfxParticle particle = g_Particles[first + (start + instanceId) % max(capacity, 1u)];
    GpuVfxEmitter emitter = g_Emitters[emitterSlot];
    if (VfxParticleIsAlive(particle) == false ||
        VfxParticleEmitterSlot(particle) != emitterSlot ||
        VfxParticleGeneration(particle) != emitter.generation)
    {
        return VfxCulledVertex();
    }

    float lifetime = particle.velocityLifetime.w;
    float age = particle.positionAge.w;
    float life = saturate(age / lifetime);

    float3 position = particle.positionAge.xyz;
    float3 velocity = particle.velocityLifetime.xyz;
    if ((emitter.flags & VFX_FLAG_LOCAL_SPACE) != 0u)
    {
        position = mul(float4(position, 1.0f), emitter.localToOrigin).xyz;
        velocity = mul(float4(velocity, 0.0f), emitter.localToOrigin).xyz;
    }

    // Camera-relative, matching the view projection. The subtraction is the only large-number arithmetic.
    float3 relative = position + (emitter.origin.xyz - cameraOrigin.xyz);

    float2 corner = float2(float(vertexId & 1u), float(vertexId >> 1u));
    float2 centred = corner * 2.0f - 1.0f;
    float halfSize = 0.5f * particle.shape.x * VfxSampleSizeCurve(emitter, life);

    float3 right;
    float3 up;
    float rotation = particle.shape.y;
    if ((emitter.flags & VFX_FLAG_ALIGN_VELOCITY) != 0u)
    {
        float speed = length(velocity);
        float3 along = speed > 1e-4f ? velocity / speed : cameraUp.xyz;
        float3 toCamera = normalize(-relative);
        float3 across = cross(along, toCamera);
        float acrossLength = length(across);
        right = acrossLength > 1e-4f ? across / acrossLength : cameraRight.xyz;
        up = along * (1.0f + speed * emitter.render.x);
        rotation = 0.0f;
    }
    else if ((emitter.flags & VFX_FLAG_ALIGN_GROUND) != 0u)
    {
        right = normalize(emitter.localToOrigin[0].xyz);
        up = normalize(emitter.localToOrigin[2].xyz);
    }
    else
    {
        right = cameraRight.xyz;
        up = cameraUp.xyz;
    }

    float sine;
    float cosine;
    sincos(rotation, sine, cosine);
    float2 rotated = float2(centred.x * cosine - centred.y * sine, centred.x * sine + centred.y * cosine);
    float3 corneredPosition = relative + (right * rotated.x + up * rotated.y) * halfSize;

    float2 uv = float2(corner.x, 1.0f - corner.y);
    uint columns = max(uint(emitter.spriteSheet.x), 1u);
    uint rows = max(uint(emitter.spriteSheet.y), 1u);
    uint frames = columns * rows;
    if (frames > 1u)
    {
        float frameRate = emitter.spriteSheet.z;
        uint frame = frameRate > 0.0f ? uint(age * frameRate) : min(uint(life * float(frames)), frames - 1u);
        if ((emitter.flags & VFX_FLAG_SHEET_RANDOM_START) != 0u)
        {
            frame += VfxHash(particle.seed) % frames;
        }

        frame %= frames;
        uv = (uv + float2(float(frame % columns), float(frame / columns))) / float2(float(columns), float(rows));
    }

    float4 color = VfxUnpackColor(particle.color) * VfxSampleColorCurve(emitter, life);
    color.rgb *= emitter.render.y;

    VfxVertexOutput output;
    output.position = mul(float4(corneredPosition, 1.0f), viewProjection);
    output.uv = uv;
    output.color = color;
    output.textureAndSampler = uint2(emitter.textureHandle, emitter.samplerHandle);
    output.depthAndSoftness = float2(output.position.w, emitter.render.z);
    return output;
}

float4 fragmentShader(VfxVertexOutput input) : SV_Target0
{
    return VfxShade(input, depthHandle, invProjection, additive != 0u, false);
}
