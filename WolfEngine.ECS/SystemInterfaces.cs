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

public interface IUpdateable: ISystem
{
	public void Update(float deltaTime, World world);
	public WorldTag GetTag();
}

public interface IPreRender: ISystem
{
	public void PreRender(float deltaTime, World world);
	public WorldTag GetTag();
}
