#define WOLF_NO_RW_TEXTURES 1
import "../Common/common_bindless.slang";
import "../Common/draw_data.slang";
import "../Common/normal_decode.slang";
#ifdef WOLF_GBUFFER_SKINNED
import "../Animation/skinning_common.slang";
#endif

#define WOLF_MAX_INSTANCE_COUNT 65535
#define WOLF_MAX_MATERIAL_COUNT 65535

StructuredBuffer<GpuInstanceData> g_InstanceTable : register(t10);
StructuredBuffer<GpuMaterialData> g_MaterialTable : register(t11);
StructuredBuffer<GpuDrawArgs> g_DrawArgsTable : register(t12);
StructuredBuffer<uint> g_MaterialGenerations : register(t13);

#ifdef WOLF_GBUFFER_SKINNED
// Rebuilds skinned vertices' previous positions for motion vectors.
StructuredBuffer<GpuSkinVertex> g_SkinVertices : register(t14);
StructuredBuffer<GpuBoneMatrix> g_BoneMatrices : register(t15);
StructuredBuffer<GpuSkinnedInstanceData> g_SkinnedInstances : register(t16);

#endif

// Written per indirect command by the command signature's root constant rather than by any binding
// call, so each draw can find its own entry in the draw args table.
cbuffer DrawIndexParams : register(b1)
{
    uint drawIndex;
};

cbuffer CameraParams : register(b2)
{
    row_major float4x4 viewProjection;
    float3 cameraPosition;
    float currentJitterPixelsX;
    float3 previousCameraPosition;
    float currentJitterPixelsY;
    row_major float4x4 unjitteredViewProjection;
    row_major float4x4 previousViewProjection;
    float2 currentJitterNdc;
    uint frameSizeX;
    uint frameSizeY;
    row_major float4x4 viewMatrix;
    float nearPlane;
    float farPlane;
    float2 _cameraPadding;
};

struct VSInput
{
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
    uint instanceId : SV_InstanceID;
    uint vertexId : SV_VertexID;
};

struct VSOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    uint instanceId : TEXCOORD3;
    float4 currentClip : TEXCOORD4;
    float4 previousClip : TEXCOORD5;
};

#ifdef WOLF_GBUFFER_SKINNED
/// Rebuilds a skinned vertex's previous object-space position.
float3 ResolvePreviousObjectPosition(VSInput input, GpuInstanceData instanceData)
{
    // Validate the full handle because table slots are recycled.
    GpuSkinnedInstanceData skinnedInstance = g_SkinnedInstances[GetHandleIndex(instanceData.meshHandle)];
    if (skinnedInstance.boneCount == 0u || skinnedInstance.meshHandle != instanceData.meshHandle)
    {
        return input.position;
    }

    // Direct3D's SV_VertexID is the index-buffer value before BaseVertexLocation is applied. Metal's
    // ICB path likewise binds the mesh's vertex range at an offset and uses base vertex zero. It is
    // therefore already mesh-local on both backends; subtracting the packed base vertex underflows
    // on Direct3D and turns this into an out-of-bounds structured-buffer read.
    uint localVertex = input.vertexId;
    if (localVertex >= skinnedInstance.vertexCount)
    {
        return input.position;
    }
    GpuSkinVertex skinVertex = g_SkinVertices[skinnedInstance.skinVertexBase + localVertex];

    return WolfSkinPosition(
        skinVertex.bindPosition.xyz,
        skinVertex.boneIndices,
        skinVertex.boneWeights,
        g_BoneMatrices,
        skinnedInstance.previousBoneMatrixOffset,
        skinnedInstance.boneCount);
}

#endif

VSOutput vertexShader(VSInput input)
{
    VSOutput output;
    GpuDrawArgs drawArgs = g_DrawArgsTable[drawIndex];
    if (drawArgs.instanceCount == 0 || drawArgs.startInstance == 0 || drawArgs.startInstance >= WOLF_MAX_INSTANCE_COUNT)
    {
        // Emit a guaranteed clipped position for inactive commands.
        output.position = float4(2.0f, 2.0f, 2.0f, 1.0f);
        output.normal = float3(0.0f, 1.0f, 0.0f);
        output.uv = float2(0.0f, 0.0f);
        output.tangent = float4(1.0f, 0.0f, 0.0f, 1.0f);
        output.instanceId = 0;
        output.currentClip = output.position;
        output.previousClip = output.position;
        return output;
    }

    uint instanceId = drawArgs.startInstance;
    GpuInstanceData instanceData = g_InstanceTable[instanceId];
    float4 worldPosition = mul(float4(input.position, 1.0f), instanceData.world);
    worldPosition.xyz -= cameraPosition;
#ifdef WOLF_GBUFFER_SKINNED
    float3 previousObjectPosition = ResolvePreviousObjectPosition(input, instanceData);
#else
    float3 previousObjectPosition = input.position;
#endif
    float4 previousWorldPosition = mul(float4(previousObjectPosition, 1.0f), instanceData.previousWorld);
    previousWorldPosition.xyz -= previousCameraPosition;
    float4 worldNormal = mul(float4(input.normal, 0.0f), instanceData.world);
    float3 worldTangent = mul(float4(input.tangent.xyz, 0.0f), instanceData.world).xyz;

    output.currentClip = mul(worldPosition, unjitteredViewProjection);
    output.position = output.currentClip;
    // Left-handed perspective projection: view-space Z equals clip W.
    output.position.xy += currentJitterNdc * output.currentClip.w;
    output.previousClip = mul(previousWorldPosition, previousViewProjection);
    output.normal = normalize(worldNormal.xyz);
    output.uv = input.uv;
    output.tangent = float4(normalize(worldTangent), input.tangent.w);
    output.instanceId = instanceId;
    return output;
}

struct PSOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
    float4 velocity : SV_Target4;
};

float2 ClipToPixel(float4 clipPosition, uint2 frameSize)
{
    float invW = abs(clipPosition.w) > 1e-5f ? rcp(clipPosition.w) : 0.0f;
    float2 ndc = clipPosition.xy * invW;
    return float2(
        (ndc.x * 0.5f + 0.5f) * frameSize.x,
        (-ndc.y * 0.5f + 0.5f) * frameSize.y);
}

float ClipToDepth(float4 clipPosition)
{
    float invW = abs(clipPosition.w) > 1e-5f ? rcp(clipPosition.w) : 0.0f;
    return clipPosition.z * invW;
}

PSOutput EvaluateFragment(VSOutput input, bool isFrontFace)
{
    PSOutput output;

    if (input.instanceId >= WOLF_MAX_INSTANCE_COUNT)
    {
        output.albedo = float4(1.0f, 0.0f, 1.0f, 1.0f);
        output.normal = float4(0.5f, 0.5f, 1.0f, 1.0f);
        output.material = float4(0.0f, 1.0f, 1.0f, 1.0f);
        output.emissive = float4(0.0f, 0.0f, 0.0f, 1.0f);
        output.velocity = float4(0.0f, 0.0f, 1.0f, 1.0f);
        return output;
    }

    GpuInstanceData instanceData = g_InstanceTable[input.instanceId];
    uint materialIndex = 0;
    uint materialHandle = instanceData.materialHandle;
    if (IsPackedHandleValid(materialHandle))
    {
        uint candidateIndex = GetHandleIndex(materialHandle);
        uint candidateGeneration = GetHandleGeneration(materialHandle);
        if (candidateIndex < WOLF_MAX_MATERIAL_COUNT &&
            g_MaterialGenerations[candidateIndex] == candidateGeneration)
        {
            materialIndex = candidateIndex;
        }
    }

    GpuMaterialData materialData = g_MaterialTable[materialIndex];
    // A double-sided lane rasterises back faces, whose interpolated normal still points away from the
    // viewer. Single-sided lanes cull them, so the flip only ever fires for geometry that asked for it.
    float3 normal = normalize(input.normal) * (isFrontFace ? 1.0f : -1.0f);

    SamplerState sampler = GetSampler(materialData.samplerHandle);
    Texture2D<float4> albedoTex = GetTexture(materialData.albedoHandle);
    Texture2D<float4> ormTex = GetTexture(materialData.ormHandle);
    Texture2D<float4> normalTex = GetTexture(materialData.normalHandle);
    Texture2D<float4> emissiveTex = GetTexture(materialData.emissiveHandle);

    float2 materialUv = input.uv * materialData.uvOffsetScale.zw + materialData.uvOffsetScale.xy;
    float4 albedoSample = albedoTex.Sample(sampler, materialUv);
    float4 ormSample = ormTex.Sample(sampler, materialUv);
#ifdef WOLF_ALPHA_CLIP
    if(albedoSample.a < materialData.metallicRoughness.z) discard;
#endif
    float3 tangentNormal = ApplyNormalMapScale(
        DecodeNormalMapSample(normalTex.Sample(sampler, materialUv)),
        materialData.metallicRoughness.w);
    float4 emissiveSample = emissiveTex.Sample(sampler, materialUv);
    float3 emissive = emissiveSample.rgb * materialData.emissiveFactorIntensity.rgb * materialData.emissiveFactorIntensity.a;
    float3 N = normal;
    float3 T = normalize(input.tangent.xyz);
    float3 B = normalize(cross(N, T) * input.tangent.w);
    float3x3 TBN = float3x3(T, B, N);
    normal = normalize(mul(tangentNormal, TBN));
   
    float occlusion = saturate(ormSample.r);
    float roughness = saturate(ormSample.g * materialData.metallicRoughness.y);
    float metallic = saturate(ormSample.b * materialData.metallicRoughness.x);
    float alphaTestReactiveMask = 0.0f;
#ifdef WOLF_ALPHA_CLIP
    // Surviving fully opaque texels follow geometry motion and should accumulate normally.
    // Only fractional coverage near a cutout edge needs reactive treatment; a binary 1 here
    // made FSR3 discard every alpha-tested sample on every jitter phase.
    alphaTestReactiveMask = saturate(1.0f - albedoSample.a);
#endif
    float4 materialParams = float4(occlusion, roughness, metallic, alphaTestReactiveMask);

    output.albedo = materialData.baseColor * albedoSample;
    output.normal = float4(normal * 0.5f + 0.5f, 1.0f);
    output.material = materialParams;
    output.emissive = float4(emissive, 1.0f);
    uint2 frameSize = uint2(max(frameSizeX, 1u), max(frameSizeY, 1u));
    float2 currentPixel = ClipToPixel(input.currentClip, frameSize);
    float2 previousPixel = ClipToPixel(input.previousClip, frameSize);
    float previousDepth = ClipToDepth(input.previousClip);
    output.velocity = float4(previousPixel - currentPixel, previousDepth, 1.0f);

    return output;
}

PSOutput fragmentShader(VSOutput input, bool isFrontFace : SV_IsFrontFace)
{
    return EvaluateFragment(input, isFrontFace);
}
