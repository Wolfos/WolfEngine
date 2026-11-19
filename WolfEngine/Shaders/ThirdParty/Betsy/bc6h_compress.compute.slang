// Copyright 2020-2022 Matias N. Goldberg
// Derived from Betsy's bc6h.glsl, which is based on GPURealTimeBC6H by Krzysztof Narkowicz (public domain).
// Ported to Slang for WolfEngine's offline GPU texture import path. See LICENSE.md alongside this file.
//
// Encodes unsigned half-float BC6H (BC6H_UF16) blocks with Betsy's QUALITY path: every block is encoded in mode 11
// (one region, 10-bit endpoints), then each of the 32 two-region partitions is tried in modes 1 and 2, and the
// encoding with the lowest mean squared log error is kept.
//
// Differences from Betsy:
// - Texels are read from a buffer of packed half-float RGBA instead of gathered from a sampled texture, and blocks
//   are written to a buffer instead of an image, matching the other import compressors.
// - Texels are clamped to [0, 65504]: the unsigned mode cannot store negative values.
// - Uniform blocks and regions, where Betsy divides by zero and relies on the GPU's NaN handling, take index 0
//   explicitly.

cbuffer Params : register(b11)
{
	uint g_width;
	uint g_height;
	uint g_blocksX;
	uint g_reserved;
}

RWStructuredBuffer<uint2> g_sourcePixels : register(u0);
RWStructuredBuffer<uint4> g_outputBlocks : register(u1);

static const float HALF_MAX = 65504.0f;

// The half-float bit pattern of each component, as a float. Betsy's f32tof16 overloads; renamed so they do not
// shadow the HLSL intrinsic they are built on.
float3 HalfBits(float3 value)
{
	return float3(
		float(f32tof16(value.x)),
		float(f32tof16(value.y)),
		float(f32tof16(value.z)));
}

float HalfBits(float value)
{
	return float(f32tof16(value));
}

float3 HalfBitsToFloat(uint3 value)
{
	return float3(
		f16tof32(value.x),
		f16tof32(value.y),
		f16tof32(value.z));
}

float3 LoadTexel(uint2 pixelCoord)
{
	uint x = min(pixelCoord.x, g_width - 1u);
	uint y = min(pixelCoord.y, g_height - 1u);
	uint2 packed = g_sourcePixels[y * g_width + x];
	float3 rgb = float3(
		f16tof32(packed.x & 0xFFFFu),
		f16tof32(packed.x >> 16u),
		f16tof32(packed.y & 0xFFFFu));

	return min(max(rgb, 0.0f), HALF_MAX);
}

float CalcMSLE(float3 a, float3 b)
{
	float3 err = log2((b + 1.0f) / (a + 1.0f));
	err = err * err;
	return err.x + err.y + err.z;
}

uint PatternFixupID(uint i)
{
	uint ret = 15u;
	ret = ((3441033216u >> i) & 0x1u) != 0u ? 2u : ret;
	ret = ((845414400u >> i) & 0x1u) != 0u ? 8u : ret;
	return ret;
}

uint Pattern(uint p, uint i)
{
	uint p2 = p / 2u;
	uint p3 = p - p2 * 2u;

	uint enc = 0u;
	enc = p2 == 0u ? 2290666700u : enc;
	enc = p2 == 1u ? 3972591342u : enc;
	enc = p2 == 2u ? 4276930688u : enc;
	enc = p2 == 3u ? 3967876808u : enc;
	enc = p2 == 4u ? 4293707776u : enc;
	enc = p2 == 5u ? 3892379264u : enc;
	enc = p2 == 6u ? 4278255592u : enc;
	enc = p2 == 7u ? 4026597360u : enc;
	enc = p2 == 8u ? 9369360u : enc;
	enc = p2 == 9u ? 147747072u : enc;
	enc = p2 == 10u ? 1930428556u : enc;
	enc = p2 == 11u ? 2362323200u : enc;
	enc = p2 == 12u ? 823134348u : enc;
	enc = p2 == 13u ? 913073766u : enc;
	enc = p2 == 14u ? 267393000u : enc;
	enc = p2 == 15u ? 966553998u : enc;

	enc = p3 != 0u ? enc >> 16u : enc;
	uint ret = (enc >> i) & 0x1u;
	return ret;
}

float3 Quantize7(float3 x)
{
	return (HalfBits(x) * 128.0f) / (float(0x7bff) + 1.0f);
}

float3 Quantize9(float3 x)
{
	return (HalfBits(x) * 512.0f) / (float(0x7bff) + 1.0f);
}

float3 Quantize10(float3 x)
{
	return (HalfBits(x) * 1024.0f) / (float(0x7bff) + 1.0f);
}

float3 Unquantize7(float3 x)
{
	return (x * 65536.0f + float(0x8000)) / 128.0f;
}

float3 Unquantize9(float3 x)
{
	return (x * 65536.0f + float(0x8000)) / 512.0f;
}

float3 Unquantize10(float3 x)
{
	return (x * 65536.0f + float(0x8000)) / 1024.0f;
}

float3 FinishUnquantize(float3 endpoint0Unq, float3 endpoint1Unq, float weight)
{
	float3 comp = (endpoint0Unq * (64.0f - weight) + endpoint1Unq * weight + 32.0f) * (31.0f / 4096.0f);
	return HalfBitsToFloat(uint3(comp));
}

void Swap(inout float3 a, inout float3 b)
{
	float3 tmp = a;
	a = b;
	b = tmp;
}

void Swap(inout float a, inout float b)
{
	float tmp = a;
	a = b;
	b = tmp;
}

// The axis texels are projected on, normalised so its components sum to one. A uniform block has no extent to
// measure; any axis then projects every texel to the same position.
float3 NormalizeBlockDirection(float3 blockDir)
{
	float sum = blockDir.x + blockDir.y + blockDir.z;
	if (sum <= 0.0f)
	{
		return float3(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f);
	}

	return blockDir / sum;
}

float EndpointRatio(float texelPos, float endPoint0Pos, float endPoint1Pos)
{
	float range = endPoint1Pos - endPoint0Pos;
	if (range == 0.0f)
	{
		return 0.0f;
	}

	return (texelPos - endPoint0Pos) / range;
}

uint ComputeIndex3(float texelPos, float endPoint0Pos, float endPoint1Pos)
{
	float r = EndpointRatio(texelPos, endPoint0Pos, endPoint1Pos);
	return uint(clamp(r * 6.98182f + 0.00909f + 0.5f, 0.0f, 7.0f));
}

uint ComputeIndex4(float texelPos, float endPoint0Pos, float endPoint1Pos)
{
	float r = EndpointRatio(texelPos, endPoint0Pos, endPoint1Pos);
	return uint(clamp(r * 14.93333f + 0.03333f + 0.5f, 0.0f, 15.0f));
}

void SignExtend(inout float3 v1, uint mask, uint signFlag)
{
	int3 v = int3(v1);
	v.x = (v.x & int(mask)) | (v.x < 0 ? int(signFlag) : 0);
	v.y = (v.y & int(mask)) | (v.y < 0 ? int(signFlag) : 0);
	v.z = (v.z & int(mask)) | (v.z < 0 ? int(signFlag) : 0);
	v1 = float3(v);
}

void EncodeP1(inout uint4 block, inout float blockMSLE, float3 texels[16])
{
	// compute endpoints (min/max RGB bbox)
	float3 blockMin = texels[0];
	float3 blockMax = texels[0];
	for (uint i = 1u; i < 16u; ++i)
	{
		blockMin = min(blockMin, texels[i]);
		blockMax = max(blockMax, texels[i]);
	}

	// refine endpoints in log2 RGB space
	float3 refinedBlockMin = blockMax;
	float3 refinedBlockMax = blockMin;
	for (uint i = 0u; i < 16u; ++i)
	{
		refinedBlockMin = min(refinedBlockMin, all(texels[i] == blockMin) ? refinedBlockMin : texels[i]);
		refinedBlockMax = max(refinedBlockMax, all(texels[i] == blockMax) ? refinedBlockMax : texels[i]);
	}

	float3 logBlockMax = log2(blockMax + 1.0f);
	float3 logBlockMin = log2(blockMin + 1.0f);
	float3 logRefinedBlockMax = log2(refinedBlockMax + 1.0f);
	float3 logRefinedBlockMin = log2(refinedBlockMin + 1.0f);
	float3 logBlockMaxExt = (logBlockMax - logBlockMin) * (1.0f / 32.0f);
	logBlockMin += min(logRefinedBlockMin - logBlockMin, logBlockMaxExt);
	logBlockMax -= min(logBlockMax - logRefinedBlockMax, logBlockMaxExt);
	blockMin = exp2(logBlockMin) - 1.0f;
	blockMax = exp2(logBlockMax) - 1.0f;

	float3 blockDir = NormalizeBlockDirection(blockMax - blockMin);

	float3 endpoint0 = Quantize10(blockMin);
	float3 endpoint1 = Quantize10(blockMax);
	float endPoint0Pos = HalfBits(dot(blockMin, blockDir));
	float endPoint1Pos = HalfBits(dot(blockMax, blockDir));

	// check if endpoint swap is required
	float fixupTexelPos = HalfBits(dot(texels[0], blockDir));
	uint fixupIndex = ComputeIndex4(fixupTexelPos, endPoint0Pos, endPoint1Pos);
	if (fixupIndex > 7u)
	{
		Swap(endPoint0Pos, endPoint1Pos);
		Swap(endpoint0, endpoint1);
	}

	// compute indices
	uint indices[16] = { 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u };
	for (uint i = 0u; i < 16u; ++i)
	{
		float texelPos = HalfBits(dot(texels[i], blockDir));
		indices[i] = ComputeIndex4(texelPos, endPoint0Pos, endPoint1Pos);
	}

	// compute compression error (MSLE)
	float3 endpoint0Unq = Unquantize10(endpoint0);
	float3 endpoint1Unq = Unquantize10(endpoint1);
	float msle = 0.0f;
	for (uint i = 0u; i < 16u; ++i)
	{
		float weight = floor((float(indices[i]) * 64.0f) / 15.0f + 0.5f);
		float3 texelUnc = FinishUnquantize(endpoint0Unq, endpoint1Unq, weight);

		msle += CalcMSLE(texels[i], texelUnc);
	}

	// encode block for mode 11
	blockMSLE = msle;
	block.x = 0x03u;

	// endpoints
	block.x |= uint(endpoint0.x) << 5u;
	block.x |= uint(endpoint0.y) << 15u;
	block.x |= uint(endpoint0.z) << 25u;
	block.y |= uint(endpoint0.z) >> 7u;
	block.y |= uint(endpoint1.x) << 3u;
	block.y |= uint(endpoint1.y) << 13u;
	block.y |= uint(endpoint1.z) << 23u;
	block.z |= uint(endpoint1.z) >> 9u;

	// indices
	block.z |= indices[0] << 1u;
	block.z |= indices[1] << 4u;
	block.z |= indices[2] << 8u;
	block.z |= indices[3] << 12u;
	block.z |= indices[4] << 16u;
	block.z |= indices[5] << 20u;
	block.z |= indices[6] << 24u;
	block.z |= indices[7] << 28u;
	block.w |= indices[8] << 0u;
	block.w |= indices[9] << 4u;
	block.w |= indices[10] << 8u;
	block.w |= indices[11] << 12u;
	block.w |= indices[12] << 16u;
	block.w |= indices[13] << 20u;
	block.w |= indices[14] << 24u;
	block.w |= indices[15] << 28u;
}

void EncodeP2Pattern(inout uint4 block, inout float blockMSLE, uint pattern, float3 texels[16])
{
	float3 p0BlockMin = float3(HALF_MAX, HALF_MAX, HALF_MAX);
	float3 p0BlockMax = float3(0.0f, 0.0f, 0.0f);
	float3 p1BlockMin = float3(HALF_MAX, HALF_MAX, HALF_MAX);
	float3 p1BlockMax = float3(0.0f, 0.0f, 0.0f);

	for (uint i = 0u; i < 16u; ++i)
	{
		uint paletteID = Pattern(pattern, i);
		if (paletteID == 0u)
		{
			p0BlockMin = min(p0BlockMin, texels[i]);
			p0BlockMax = max(p0BlockMax, texels[i]);
		}
		else
		{
			p1BlockMin = min(p1BlockMin, texels[i]);
			p1BlockMax = max(p1BlockMax, texels[i]);
		}
	}

	float3 p0BlockDir = NormalizeBlockDirection(p0BlockMax - p0BlockMin);
	float3 p1BlockDir = NormalizeBlockDirection(p1BlockMax - p1BlockMin);

	float p0Endpoint0Pos = HalfBits(dot(p0BlockMin, p0BlockDir));
	float p0Endpoint1Pos = HalfBits(dot(p0BlockMax, p0BlockDir));
	float p1Endpoint0Pos = HalfBits(dot(p1BlockMin, p1BlockDir));
	float p1Endpoint1Pos = HalfBits(dot(p1BlockMax, p1BlockDir));

	uint fixupID = PatternFixupID(pattern);
	float p0FixupTexelPos = HalfBits(dot(texels[0], p0BlockDir));
	float p1FixupTexelPos = HalfBits(dot(texels[fixupID], p1BlockDir));
	uint p0FixupIndex = ComputeIndex3(p0FixupTexelPos, p0Endpoint0Pos, p0Endpoint1Pos);
	uint p1FixupIndex = ComputeIndex3(p1FixupTexelPos, p1Endpoint0Pos, p1Endpoint1Pos);
	if (p0FixupIndex > 3u)
	{
		Swap(p0Endpoint0Pos, p0Endpoint1Pos);
		Swap(p0BlockMin, p0BlockMax);
	}

	if (p1FixupIndex > 3u)
	{
		Swap(p1Endpoint0Pos, p1Endpoint1Pos);
		Swap(p1BlockMin, p1BlockMax);
	}

	uint indices[16] = { 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u };
	for (uint i = 0u; i < 16u; ++i)
	{
		float p0TexelPos = HalfBits(dot(texels[i], p0BlockDir));
		float p1TexelPos = HalfBits(dot(texels[i], p1BlockDir));
		uint p0Index = ComputeIndex3(p0TexelPos, p0Endpoint0Pos, p0Endpoint1Pos);
		uint p1Index = ComputeIndex3(p1TexelPos, p1Endpoint0Pos, p1Endpoint1Pos);

		uint paletteID = Pattern(pattern, i);
		indices[i] = paletteID == 0u ? p0Index : p1Index;
	}

	float3 endpoint760 = floor(Quantize7(p0BlockMin));
	float3 endpoint761 = floor(Quantize7(p0BlockMax));
	float3 endpoint762 = floor(Quantize7(p1BlockMin));
	float3 endpoint763 = floor(Quantize7(p1BlockMax));

	float3 endpoint950 = floor(Quantize9(p0BlockMin));
	float3 endpoint951 = floor(Quantize9(p0BlockMax));
	float3 endpoint952 = floor(Quantize9(p1BlockMin));
	float3 endpoint953 = floor(Quantize9(p1BlockMax));

	endpoint761 = endpoint761 - endpoint760;
	endpoint762 = endpoint762 - endpoint760;
	endpoint763 = endpoint763 - endpoint760;

	endpoint951 = endpoint951 - endpoint950;
	endpoint952 = endpoint952 - endpoint950;
	endpoint953 = endpoint953 - endpoint950;

	float maxVal76 = float(0x1F);
	endpoint761 = clamp(endpoint761, -maxVal76, maxVal76);
	endpoint762 = clamp(endpoint762, -maxVal76, maxVal76);
	endpoint763 = clamp(endpoint763, -maxVal76, maxVal76);

	float maxVal95 = float(0xF);
	endpoint951 = clamp(endpoint951, -maxVal95, maxVal95);
	endpoint952 = clamp(endpoint952, -maxVal95, maxVal95);
	endpoint953 = clamp(endpoint953, -maxVal95, maxVal95);

	float3 endpoint760Unq = Unquantize7(endpoint760);
	float3 endpoint761Unq = Unquantize7(endpoint760 + endpoint761);
	float3 endpoint762Unq = Unquantize7(endpoint760 + endpoint762);
	float3 endpoint763Unq = Unquantize7(endpoint760 + endpoint763);
	float3 endpoint950Unq = Unquantize9(endpoint950);
	float3 endpoint951Unq = Unquantize9(endpoint950 + endpoint951);
	float3 endpoint952Unq = Unquantize9(endpoint950 + endpoint952);
	float3 endpoint953Unq = Unquantize9(endpoint950 + endpoint953);

	float msle76 = 0.0f;
	float msle95 = 0.0f;
	for (uint i = 0u; i < 16u; ++i)
	{
		uint paletteID = Pattern(pattern, i);

		float3 tmp760Unq = paletteID == 0u ? endpoint760Unq : endpoint762Unq;
		float3 tmp761Unq = paletteID == 0u ? endpoint761Unq : endpoint763Unq;
		float3 tmp950Unq = paletteID == 0u ? endpoint950Unq : endpoint952Unq;
		float3 tmp951Unq = paletteID == 0u ? endpoint951Unq : endpoint953Unq;

		float weight = floor((float(indices[i]) * 64.0f) / 7.0f + 0.5f);
		float3 texelUnc76 = FinishUnquantize(tmp760Unq, tmp761Unq, weight);
		float3 texelUnc95 = FinishUnquantize(tmp950Unq, tmp951Unq, weight);

		msle76 += CalcMSLE(texels[i], texelUnc76);
		msle95 += CalcMSLE(texels[i], texelUnc95);
	}

	SignExtend(endpoint761, 0x1Fu, 0x20u);
	SignExtend(endpoint762, 0x1Fu, 0x20u);
	SignExtend(endpoint763, 0x1Fu, 0x20u);

	SignExtend(endpoint951, 0xFu, 0x10u);
	SignExtend(endpoint952, 0xFu, 0x10u);
	SignExtend(endpoint953, 0xFu, 0x10u);

	// encode block
	float p2MSLE = min(msle76, msle95);
	if (p2MSLE < blockMSLE)
	{
		blockMSLE = p2MSLE;
		block = uint4(0u, 0u, 0u, 0u);

		if (p2MSLE == msle76)
		{
			// 7.6
			block.x = 0x1u;
			block.x |= (uint(endpoint762.y) & 0x20u) >> 3u;
			block.x |= (uint(endpoint763.y) & 0x10u) >> 1u;
			block.x |= (uint(endpoint763.y) & 0x20u) >> 1u;
			block.x |= uint(endpoint760.x) << 5u;
			block.x |= (uint(endpoint763.z) & 0x01u) << 12u;
			block.x |= (uint(endpoint763.z) & 0x02u) << 12u;
			block.x |= (uint(endpoint762.z) & 0x10u) << 10u;
			block.x |= uint(endpoint760.y) << 15u;
			block.x |= (uint(endpoint762.z) & 0x20u) << 17u;
			block.x |= (uint(endpoint763.z) & 0x04u) << 21u;
			block.x |= (uint(endpoint762.y) & 0x10u) << 20u;
			block.x |= uint(endpoint760.z) << 25u;
			block.y |= (uint(endpoint763.z) & 0x08u) >> 3u;
			block.y |= (uint(endpoint763.z) & 0x20u) >> 4u;
			block.y |= (uint(endpoint763.z) & 0x10u) >> 2u;
			block.y |= uint(endpoint761.x) << 3u;
			block.y |= (uint(endpoint762.y) & 0x0Fu) << 9u;
			block.y |= uint(endpoint761.y) << 13u;
			block.y |= (uint(endpoint763.y) & 0x0Fu) << 19u;
			block.y |= uint(endpoint761.z) << 23u;
			block.y |= (uint(endpoint762.z) & 0x07u) << 29u;
			block.z |= (uint(endpoint762.z) & 0x08u) >> 3u;
			block.z |= uint(endpoint762.x) << 1u;
			block.z |= uint(endpoint763.x) << 7u;
		}
		else
		{
			// 9.5
			block.x = 0xEu;
			block.x |= uint(endpoint950.x) << 5u;
			block.x |= (uint(endpoint952.z) & 0x10u) << 10u;
			block.x |= uint(endpoint950.y) << 15u;
			block.x |= (uint(endpoint952.y) & 0x10u) << 20u;
			block.x |= uint(endpoint950.z) << 25u;
			block.y |= uint(endpoint950.z) >> 7u;
			block.y |= (uint(endpoint953.z) & 0x10u) >> 2u;
			block.y |= uint(endpoint951.x) << 3u;
			block.y |= (uint(endpoint953.y) & 0x10u) << 4u;
			block.y |= (uint(endpoint952.y) & 0x0Fu) << 9u;
			block.y |= uint(endpoint951.y) << 13u;
			block.y |= (uint(endpoint953.z) & 0x01u) << 18u;
			block.y |= (uint(endpoint953.y) & 0x0Fu) << 19u;
			block.y |= uint(endpoint951.z) << 23u;
			block.y |= (uint(endpoint953.z) & 0x02u) << 27u;
			block.y |= uint(endpoint952.z) << 29u;
			block.z |= (uint(endpoint952.z) & 0x08u) >> 3u;
			block.z |= uint(endpoint952.x) << 1u;
			block.z |= (uint(endpoint953.z) & 0x04u) << 4u;
			block.z |= uint(endpoint953.x) << 7u;
			block.z |= (uint(endpoint953.z) & 0x08u) << 9u;
		}

		block.z |= pattern << 13u;
		uint blockFixupID = PatternFixupID(pattern);
		if (blockFixupID == 15u)
		{
			block.z |= indices[0] << 18u;
			block.z |= indices[1] << 20u;
			block.z |= indices[2] << 23u;
			block.z |= indices[3] << 26u;
			block.z |= indices[4] << 29u;
			block.w |= indices[5] << 0u;
			block.w |= indices[6] << 3u;
			block.w |= indices[7] << 6u;
			block.w |= indices[8] << 9u;
			block.w |= indices[9] << 12u;
			block.w |= indices[10] << 15u;
			block.w |= indices[11] << 18u;
			block.w |= indices[12] << 21u;
			block.w |= indices[13] << 24u;
			block.w |= indices[14] << 27u;
			block.w |= indices[15] << 30u;
		}
		else if (blockFixupID == 2u)
		{
			block.z |= indices[0] << 18u;
			block.z |= indices[1] << 20u;
			block.z |= indices[2] << 23u;
			block.z |= indices[3] << 25u;
			block.z |= indices[4] << 28u;
			block.z |= indices[5] << 31u;
			block.w |= indices[5] >> 1u;
			block.w |= indices[6] << 2u;
			block.w |= indices[7] << 5u;
			block.w |= indices[8] << 8u;
			block.w |= indices[9] << 11u;
			block.w |= indices[10] << 14u;
			block.w |= indices[11] << 17u;
			block.w |= indices[12] << 20u;
			block.w |= indices[13] << 23u;
			block.w |= indices[14] << 26u;
			block.w |= indices[15] << 29u;
		}
		else
		{
			block.z |= indices[0] << 18u;
			block.z |= indices[1] << 20u;
			block.z |= indices[2] << 23u;
			block.z |= indices[3] << 26u;
			block.z |= indices[4] << 29u;
			block.w |= indices[5] << 0u;
			block.w |= indices[6] << 3u;
			block.w |= indices[7] << 6u;
			block.w |= indices[8] << 9u;
			block.w |= indices[9] << 11u;
			block.w |= indices[10] << 14u;
			block.w |= indices[11] << 17u;
			block.w |= indices[12] << 20u;
			block.w |= indices[13] << 23u;
			block.w |= indices[14] << 26u;
			block.w |= indices[15] << 29u;
		}
	}
}

[numthreads(8, 8, 1)]
void CompressBc6h(uint3 dispatchThreadId : SV_DispatchThreadID)
{
	const uint blocksY = max(1u, (g_height + 3u) / 4u);
	if (dispatchThreadId.x >= g_blocksX || dispatchThreadId.y >= blocksY)
	{
		return;
	}

	// gather texels for current 4x4 block
	// 0 1 2 3
	// 4 5 6 7
	// 8 9 10 11
	// 12 13 14 15
	const uint2 blockOrigin = dispatchThreadId.xy * 4u;
	float3 texels[16];
	for (uint i = 0u; i < 16u; ++i)
	{
		texels[i] = LoadTexel(blockOrigin + uint2(i % 4u, i / 4u));
	}

	uint4 block = uint4(0u, 0u, 0u, 0u);
	float blockMSLE = 0.0f;

	EncodeP1(block, blockMSLE, texels);

	[loop]
	for (uint pattern = 0u; pattern < 32u; ++pattern)
	{
		EncodeP2Pattern(block, blockMSLE, pattern, texels);
	}

	g_outputBlocks[dispatchThreadId.y * g_blocksX + dispatchThreadId.x] = block;
}
