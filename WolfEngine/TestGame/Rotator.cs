using WolfEngine.ECS;

namespace WolfEngine.TestGame;

public struct Rotator: IEntityComponent
{
	public float RotationSpeed;
	public float CurrentRotation;
}