namespace WolfEngine.ECS;

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