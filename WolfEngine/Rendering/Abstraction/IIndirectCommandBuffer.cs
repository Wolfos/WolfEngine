namespace WolfEngine.Rendering.Abstraction;

public interface IIndirectCommandBuffer
{
	public void Reset(uint commandCount);
	// TODO: Proper abstraction
	SharpMetal.Metal.MTLIndirectRenderCommand GetRenderCommand(uint index);
}