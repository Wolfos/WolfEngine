using WolfEngine.ECS;

namespace WolfEngine.Gameplay;

public interface IGameplayModule
{
	void OnLoaded(World world);
	void OnUnloading(World world);
	void Update(float deltaTime, World world);
}
