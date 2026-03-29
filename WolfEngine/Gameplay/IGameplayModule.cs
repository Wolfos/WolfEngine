using WolfEngine.ECS;

namespace WolfEngine.Gameplay;

public interface IGameplayModule
{
	IEnumerable<ISystem> CreateSystems() => Array.Empty<ISystem>();
	void OnLoaded(World world);
	void OnUnloading(World world);
	void PhysicsUpdate(float fixedDeltaTime, World world);
	void Update(float deltaTime, World world);
}
