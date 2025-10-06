namespace WolfEngine;

public interface IRenderer
{
	void SubmitCommand(RenderCommand command);
	void Run(Action update);
}
