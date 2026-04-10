namespace WolfEngine.ECS;

public enum SystemExecutionGroup
{
	None = 0,
	Shared = 1,
	Gameplay = 2,
	All = Shared | Gameplay
}

public interface ISystem
{
}

public interface IUpdate: ISystem
{
	public void Update(float deltaTime, World world);
	public WorldTag GetTag();
}

public interface IPhysicsUpdate: ISystem
{
	public void PhysicsUpdate(float fixedDeltaTime, World world);
	public WorldTag GetTag();
}

public interface IPreRender: ISystem
{
	public void PreRender(float deltaTime, World world);
	public WorldTag GetTag();
}

public interface IOnDrawGizmos : ISystem
{
	public void OnDrawGizmos(World world);
	public WorldTag GetTag();
}

public interface IWorldRemovedListener: ISystem
{
	public void OnWorldRemoved(World world);
}
