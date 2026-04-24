using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public enum PhysicsContactEventType
{
	Added,
	Persisted,
	Removed
}

public readonly record struct PhysicsRaycastHit(
	Entity Entity,
	Vector3 Point,
	Vector3 Normal,
	float Fraction,
	bool IsSensor,
	uint Layer);

public readonly record struct PhysicsShapeCastHit(
	Entity Entity,
	Vector3 Point,
	Vector3 Normal,
	float Fraction,
	float PenetrationDepth,
	bool IsSensor,
	uint Layer);

public readonly record struct PhysicsOverlapHit(
	Entity Entity,
	Vector3 Point,
	Vector3 Normal,
	float PenetrationDepth,
	bool IsSensor,
	uint Layer);

public readonly record struct TerrainSurfaceSample(
	Entity Entity,
	Vector3 Point,
	Vector3 Normal);

public readonly record struct PhysicsContactEvent(
	PhysicsContactEventType EventType,
	Entity EntityA,
	Entity EntityB,
	Vector3 Normal,
	Vector3 PointOnA,
	Vector3 PointOnB,
	float PenetrationDepth,
	bool IsSensor);
