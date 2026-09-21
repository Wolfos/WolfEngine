using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RenderGraphTransientAliasSlotTests
{
	[Test]
	public void UnusedTextureHandleCannotCollideWithACompiledAliasSlot()
	{
		var registry = new RenderGraphResourceRegistry();
		var unused = registry.CreateTransientTexture(new TextureDescriptor(
			256, 256, TextureFormat.Rgba8Unorm, TextureUsage.ShaderResource | TextureUsage.RenderTarget));
		var used = registry.CreateTransientTexture(new TextureDescriptor(
			1280, 720, TextureFormat.Rgba16Float, TextureUsage.ShaderResource | TextureUsage.RenderTarget));

		Assert.That(unused.Id, Is.EqualTo(1));
		Assert.DoesNotThrow(() => registry.AssignTransientTextureSlots(new Dictionary<int, int>
		{
			[used.Id] = 1
		}));
		Assert.That(registry.GetStateTrackingKey(unused), Is.Not.EqualTo(registry.GetStateTrackingKey(used)));
	}
}
