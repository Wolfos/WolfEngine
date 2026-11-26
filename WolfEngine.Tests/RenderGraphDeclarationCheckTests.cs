using Moq;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

/// <summary>
/// A graph node may only fetch the resources it declared, because the graph derives barriers and transient memory
/// sharing from declarations alone. Debug builds enforce that in <see cref="RenderGraphContext"/>.
/// </summary>
[TestFixture]
public sealed class RenderGraphDeclarationCheckTests
{
	[Test]
	public void ANodeCanFetchTheTexturesItRead()
	{
		var registry = new RenderGraphResourceRegistry();
		var texture = ImportTexture(registry);
		var node = new RenderGraphPass("Reader", PassKind.Compute);
		node.AddRead(texture.Handle);
		var context = new RenderGraphContext(registry, node);

		Assert.That(context.GetTexture(texture.Handle), Is.SameAs(texture.Texture));
	}

	[Test]
	public void ANodeCanFetchTheBuffersItWrote()
	{
		var registry = new RenderGraphResourceRegistry();
		var buffer = Mock.Of<IGfxBuffer>();
		var handle = registry.ImportBuffer(buffer);
		var node = new RenderGraphPass("Writer", PassKind.Compute);
		node.AddWrite(handle, ResourceState.UnorderedAccess);
		var context = new RenderGraphContext(registry, node);

		Assert.That(context.GetBuffer(handle), Is.SameAs(buffer));
	}

#if DEBUG
	[Test]
	public void FetchingAnUndeclaredTextureThrowsAndNamesTheNode()
	{
		var registry = new RenderGraphResourceRegistry();
		var declared = ImportTexture(registry);
		var undeclared = ImportTexture(registry);
		var node = new RenderGraphPass("CAS Sharpen", PassKind.Compute);
		node.AddRead(declared.Handle);
		var context = new RenderGraphContext(registry, node);

		var exception = Assert.Throws<InvalidOperationException>(() => context.GetTexture(undeclared.Handle));

		Assert.That(exception!.Message, Does.Contain("CAS Sharpen"));
	}

	[Test]
	public void FetchingAnUndeclaredBufferThrows()
	{
		var registry = new RenderGraphResourceRegistry();
		var handle = registry.ImportBuffer(Mock.Of<IGfxBuffer>());
		var context = new RenderGraphContext(registry, new RenderGraphPass("Empty", PassKind.Compute));

		Assert.Throws<InvalidOperationException>(() => context.GetBuffer(handle));
	}
#endif

	[Test]
	public void AContextBuiltOutsideTheGraphIsNotChecked()
	{
		var registry = new RenderGraphResourceRegistry();
		var texture = ImportTexture(registry);
		var context = new RenderGraphContext(registry, "Test");

		Assert.That(context.GetTexture(texture.Handle), Is.SameAs(texture.Texture));
	}

	private static (RenderGraphResourceHandle Handle, IGfxTexture Texture) ImportTexture(RenderGraphResourceRegistry registry)
	{
		var texture = new Mock<IGfxTexture>();
		texture.SetupGet(value => value.Descriptor).Returns(new TextureDescriptor(
			4,
			4,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource));

		return (registry.ImportTexture(texture.Object), texture.Object);
	}
}
