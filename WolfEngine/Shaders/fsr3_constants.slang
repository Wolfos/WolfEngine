// FSR3 upscaler shared constant buffer.
//
// Mirrors `Fsr3UpscalerConstants` from FidelityFX SDK v1.1.2
// (ffx-fsr3/upstream/ffx_fsr3upscaler_private.h) and the matching `cbFSR3Upscaler`
// declaration in ffx_fsr3upscaler_callbacks_hlsl.h, field for field and in order.
//
// Two deliberate differences from upstream:
//
//  1. int2 pairs are declared as scalar pairs, because ShaderPropertyWriter has no
//     integer-vector setter. The packing is unchanged - each pair still occupies the
//     same two slots - and every consumer goes through the accessor functions below,
//     which is how upstream expects its passes to read constants anyway
//     ("Define getter functions in the order they are defined in the CB!").
//
//  2. Upstream binds this at whatever slot the permutation assigns. Here it is fixed
//     at b1, with b0 reserved for per-pass bindless handles and b2 for per-pass
//     constants, matching the engine's existing pass convention.
//
// Field order is load-bearing: it determines the cbuffer layout, and the C# side
// writes by name through reflection. If you reorder anything here, reorder
// Fsr3Constants.cs to match.

import "ffx_port_common.slang";

cbuffer cbFSR3Upscaler : register(b1)
{
    int iRenderSizeX;
    int iRenderSizeY;
    int iPreviousFrameRenderSizeX;
    int iPreviousFrameRenderSizeY;

    int iUpscaleSizeX;
    int iUpscaleSizeY;
    int iPreviousFrameUpscaleSizeX;
    int iPreviousFrameUpscaleSizeY;

    int iMaxRenderSizeX;
    int iMaxRenderSizeY;
    int iMaxUpscaleSizeX;
    int iMaxUpscaleSizeY;

    float4 fDeviceToViewDepth;

    float2 fJitter;
    float2 fPreviousFrameJitter;

    float2 fMotionVectorScale;
    float2 fDownscaleFactor;

    float2 fMotionVectorJitterCancellation;
    float fTanHalfFOV;
    float fJitterSequenceLength;

    float fDeltaTime;
    float fDeltaPreExposure;
    float fViewSpaceToMetersFactor;
    float fFrameIndex;

    float fVelocityFactor;
    float fFsr3Padding0;
    float fFsr3Padding1;
    float fFsr3Padding2;
};

// ---------------------------------------------------------------------------
// Accessors, in constant buffer order, matching ffx_fsr3upscaler_callbacks_hlsl.h.
// Ported pass bodies call these and never touch the cbuffer fields directly.
// ---------------------------------------------------------------------------

FfxInt32x2 RenderSize() { return FfxInt32x2(iRenderSizeX, iRenderSizeY); }
FfxInt32x2 PreviousFrameRenderSize() { return FfxInt32x2(iPreviousFrameRenderSizeX, iPreviousFrameRenderSizeY); }
FfxInt32x2 MaxRenderSize() { return FfxInt32x2(iMaxRenderSizeX, iMaxRenderSizeY); }
FfxInt32x2 UpscaleSize() { return FfxInt32x2(iUpscaleSizeX, iUpscaleSizeY); }
FfxInt32x2 PreviousFrameUpscaleSize() { return FfxInt32x2(iPreviousFrameUpscaleSizeX, iPreviousFrameUpscaleSizeY); }
FfxInt32x2 MaxUpscaleSize() { return FfxInt32x2(iMaxUpscaleSizeX, iMaxUpscaleSizeY); }

FfxFloat32x4 DeviceToViewSpaceTransformFactors() { return fDeviceToViewDepth; }

FfxFloat32x2 Jitter() { return fJitter; }
FfxFloat32x2 PreviousFrameJitter() { return fPreviousFrameJitter; }

FfxFloat32x2 MotionVectorScale() { return fMotionVectorScale; }
FfxFloat32x2 DownscaleFactor() { return fDownscaleFactor; }

FfxFloat32x2 MotionVectorJitterCancellation() { return fMotionVectorJitterCancellation; }
FfxFloat32 TanHalfFoV() { return fTanHalfFOV; }
FfxFloat32 JitterSequenceLength() { return fJitterSequenceLength; }

FfxFloat32 DeltaTime() { return fDeltaTime; }
FfxFloat32 DeltaPreExposure() { return fDeltaPreExposure; }
FfxFloat32 ViewSpaceToMetersFactor() { return fViewSpaceToMetersFactor; }
FfxFloat32 FrameIndex() { return fFrameIndex; }

FfxFloat32 VelocityFactor() { return fVelocityFactor; }
