namespace WolfEngine;

public interface IRenderer
{
	void SubmitCommand(RenderCommand command);
	void Run(Action startup, Action update);
	void CreateMaterialResources(Material material);
}
