// Integration utility for deterministic FSR3 resource initialization.
import "../../Common/common_bindless.slang";

cbuffer Fsr3ClearHandles : register(b0)
{
    uint outputHandle;
};

cbuffer Fsr3ClearConstants : register(b2)
{
    uint clearWidth;
    uint clearHeight;
    uint clearValueBits;
    uint clearPadding;
};

[numthreads(8, 8, 1)]
void Fsr3ClearFloatCS(uint2 pixel : SV_DispatchThreadID)
{
    if (any(pixel >= uint2(clearWidth, clearHeight))) return;
    float value = asfloat(clearValueBits);
    GetRWTexture(outputHandle)[pixel] = float4(value, value, value, value);
}

[numthreads(8, 8, 1)]
void Fsr3ClearUintCS(uint2 pixel : SV_DispatchThreadID)
{
    if (any(pixel >= uint2(clearWidth, clearHeight))) return;
    GetRWTextureUint(outputHandle)[pixel] = clearValueBits;
}
