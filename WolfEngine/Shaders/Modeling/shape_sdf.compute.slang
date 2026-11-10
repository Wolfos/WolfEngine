// Samples a shape's signed distance field on a regular grid for the modeling tool.
// Runs the stack program built by ShapeSdfProgram.Compile. The primitive distance functions and the smooth operators
// must match ShapePrimitives.Evaluate and ShapeSdfProgram.Combine on the CPU exactly; keep them in step.

// Packed into whole vectors: Metal gives a float3 in a constant buffer 16 bytes, so a float3 followed by a scalar
// lays out differently there than on D3D12. Matches GridConstants in GpuSdfGridSampler.cs.
cbuffer ShapeSdfGridParams : register(b11)
{
	float4 g_originAndCellSize;
	uint4 g_sizeAndInstructionCount;
}

// Matches ShapeSdfInstruction in ShapeSdfProgram.cs: 96 bytes.
struct ShapeSdfInstruction
{
	uint opcode;
	uint primitive;
	uint operation;
	float smoothness;
	float4 row0;
	float4 row1;
	float4 row2;
	float4 dimensions;
	float scale;
	float padding0;
	float padding1;
	float padding2;
};

RWStructuredBuffer<ShapeSdfInstruction> g_instructions : register(u0);
RWStructuredBuffer<float> g_distances : register(u1);

static const uint OPCODE_PUSH_EMPTY = 0u;
static const uint OPCODE_PRIMITIVE = 1u;
static const uint OPCODE_POP_COMBINE = 2u;

static const uint OPERATION_UNION = 0u;
static const uint OPERATION_SUBTRACT = 1u;
static const uint OPERATION_INTERSECT = 2u;

static const uint PRIMITIVE_SPHERE = 0u;
static const uint PRIMITIVE_BOX = 1u;
static const uint PRIMITIVE_ROUNDED_BOX = 2u;
static const uint PRIMITIVE_CAPSULE = 3u;
static const uint PRIMITIVE_CYLINDER = 4u;
static const uint PRIMITIVE_TORUS = 5u;
static const uint PRIMITIVE_CONE = 6u;

// ShapeSdfProgram.MaxStackDepth and EmptyDistance.
static const int MAX_STACK_DEPTH = 16;
static const float EMPTY_DISTANCE = 1.0e20;

float SmoothMin(float a, float b, float k)
{
	if (k <= 0.0)
	{
		return min(a, b);
	}

	float h = max(k - abs(a - b), 0.0) / k;
	return min(a, b) - h * h * k * 0.25;
}

float SmoothMax(float a, float b, float k)
{
	return -SmoothMin(-a, -b, k);
}

float Combine(uint operation, float accumulated, float distance, float smoothness)
{
	if (operation == OPERATION_UNION)
	{
		return SmoothMin(accumulated, distance, smoothness);
	}

	if (operation == OPERATION_SUBTRACT)
	{
		return SmoothMax(accumulated, -distance, smoothness);
	}

	if (operation == OPERATION_INTERSECT)
	{
		return SmoothMax(accumulated, distance, smoothness);
	}

	return accumulated;
}

float SdBox(float3 p, float3 halfExtents)
{
	float3 q = abs(p) - halfExtents;
	return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

float SdCappedCone(float3 p, float h, float r1, float r2)
{
	float2 q = float2(length(p.xz), p.y);
	float2 k1 = float2(r2, h);
	float2 k2 = float2(r2 - r1, 2.0 * h);
	float2 ca = float2(q.x - min(q.x, q.y < 0.0 ? r1 : r2), abs(q.y) - h);
	float2 cb = q - k1 + k2 * clamp(dot(k1 - q, k2) / dot(k2, k2), 0.0, 1.0);
	float s = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;
	return s * sqrt(min(dot(ca, ca), dot(cb, cb)));
}

float EvaluatePrimitive(uint primitive, float4 d, float3 p)
{
	if (primitive == PRIMITIVE_SPHERE)
	{
		return length(p) - d.x;
	}

	if (primitive == PRIMITIVE_BOX)
	{
		return SdBox(p, d.xyz);
	}

	if (primitive == PRIMITIVE_ROUNDED_BOX)
	{
		return SdBox(p, d.xyz - d.www) - d.w;
	}

	if (primitive == PRIMITIVE_CAPSULE)
	{
		float3 q = p;
		q.y -= clamp(q.y, -d.y, d.y);
		return length(q) - d.x;
	}

	if (primitive == PRIMITIVE_CYLINDER)
	{
		float2 q = float2(length(p.xz), abs(p.y)) - d.xy;
		return min(max(q.x, q.y), 0.0) + length(max(q, 0.0));
	}

	if (primitive == PRIMITIVE_TORUS)
	{
		float2 q = float2(length(p.xz) - d.x, p.y);
		return length(q) - d.y;
	}

	if (primitive == PRIMITIVE_CONE)
	{
		return SdCappedCone(p, d.y, d.x, 0.0);
	}

	return EMPTY_DISTANCE;
}

float EvaluateShape(float3 position)
{
	float stack[MAX_STACK_DEPTH];
	int top = -1;
	float4 p4 = float4(position, 1.0);

	uint instructionCount = g_sizeAndInstructionCount.w;
	for (uint i = 0u; i < instructionCount; i++)
	{
		ShapeSdfInstruction instruction = g_instructions[i];
		if (instruction.opcode == OPCODE_PUSH_EMPTY)
		{
			top++;
			stack[top] = EMPTY_DISTANCE;
		}
		else if (instruction.opcode == OPCODE_PRIMITIVE)
		{
			float3 local = float3(dot(instruction.row0, p4), dot(instruction.row1, p4), dot(instruction.row2, p4));
			float distance = EvaluatePrimitive(instruction.primitive, instruction.dimensions, local) * instruction.scale;
			stack[top] = Combine(instruction.operation, stack[top], distance, instruction.smoothness);
		}
		else if (instruction.opcode == OPCODE_POP_COMBINE)
		{
			float group = stack[top];
			top--;
			stack[top] = Combine(instruction.operation, stack[top], group, instruction.smoothness);
		}
	}

	return top >= 0 ? stack[top] : EMPTY_DISTANCE;
}

[numthreads(4, 4, 4)]
void SampleShapeSdfGrid(uint3 dispatchThreadId : SV_DispatchThreadID)
{
	uint3 size = g_sizeAndInstructionCount.xyz;
	if (dispatchThreadId.x >= size.x || dispatchThreadId.y >= size.y || dispatchThreadId.z >= size.z)
	{
		return;
	}

	float3 position = g_originAndCellSize.xyz + float3(dispatchThreadId) * g_originAndCellSize.w;
	uint index = (dispatchThreadId.z * size.y + dispatchThreadId.y) * size.x + dispatchThreadId.x;
	g_distances[index] = EvaluateShape(position);
}
