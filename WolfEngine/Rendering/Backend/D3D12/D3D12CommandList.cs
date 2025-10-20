using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Backend.D3D12;

public class D3D12CommandList: IGfxCommandList
{
	public void BeginPass(in PassTargets targets, in Viewport viewport)
	{
		throw new NotImplementedException();
	}

	public void EndPass()
	{
		throw new NotImplementedException();
	}

	public void BindPipeline(IGfxPipeline pipeline)
	{
		throw new NotImplementedException();
	}

	public void SetBindlessTable(IGfxDescriptorTable table)
	{
		throw new NotImplementedException();
	}

	public void PushConstants<T>(in T data) where T : unmanaged
	{
		throw new NotImplementedException();
	}

	public void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers)
	{
		throw new NotImplementedException();
	}

	public void SetIndexBuffer(in IndexBufferView indexBuffer)
	{
		throw new NotImplementedException();
	}

	public void Draw(in DrawArguments arguments)
	{
		throw new NotImplementedException();
	}

	public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
	{
		throw new NotImplementedException();
	}

	public void Barrier(in ResourceBarrierDescription barrier)
	{
		throw new NotImplementedException();
	}
}