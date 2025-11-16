namespace WolfEngine.Rendering.Abstraction;

public interface IMaterialResources
{
	IGfxPipeline Pipeline { get; }
	IGfxBuffer? ConstantBuffer { get; }
}