using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

public sealed class GraphicsPassBindingSetTests
{
	[Test]
	public void TransparentReflectionBuildsExpectedPassBindingsAndExcludesBindlessTables()
	{
		var reflection = new ShaderReflectionLayout(
		[
			Constant("TransparentEnvironmentParams", 0), Constant("CameraParams", 2),
			Constant("LightingParams", 3), Constant("BindlessCounts", 27)
		],
		[
			new ShaderResourceBindingLayout("g_InstanceTable", 10),
			new ShaderResourceBindingLayout("g_MaterialTable", 11),
			new ShaderResourceBindingLayout("g_DrawArgsTable", 12),
			new ShaderResourceBindingLayout("g_MaterialGenerations", 13),
			new ShaderResourceBindingLayout("g_PointLights", 14),
			new ShaderResourceBindingLayout("g_ClusterHeaders", 15),
			new ShaderResourceBindingLayout("g_ClusterLightIndices", 16),
			new ShaderResourceBindingLayout("g_TextureHeap", 28),
			new ShaderResourceBindingLayout("g_RWTextures", 0),
			new ShaderResourceBindingLayout("g_RWTexturesUint", 0),
			new ShaderResourceBindingLayout("g_RWTexturesCoherent", 0)
		]);
		var resources = Resources("TransparentEnvironmentParams", "CameraParams", "LightingParams",
			"g_PointLights", "g_ClusterHeaders", "g_ClusterLightIndices");

		var bindings = GraphicsPassBindingSet.FromReflection(
			reflection, resources, SharedDrawPerDrawBindings.ResourceNames).Bindings;

		Assert.That(BindingSlots(bindings), Is.EquivalentTo(new[]
		{
			(GraphicsPassBindingKind.ConstantBuffer, 0u),
			(GraphicsPassBindingKind.ConstantBuffer, 2u),
			(GraphicsPassBindingKind.ConstantBuffer, 3u),
			(GraphicsPassBindingKind.StructuredBuffer, 14u),
			(GraphicsPassBindingKind.StructuredBuffer, 15u),
			(GraphicsPassBindingKind.StructuredBuffer, 16u)
		}));
	}

	[Test]
	public void GBufferAndShadowBindingsAreDeclaredOnlyByTheirReflection()
	{
		var gbuffer = new ShaderReflectionLayout([Constant("CameraParams", 2)]);
		var shadow = new ShaderReflectionLayout([Constant("CameraParams", 16)],
			[new ShaderResourceBindingLayout("g_TerrainMaterialTable", 14)]);

		Assert.That(BindingRegisters(GraphicsPassBindingSet.FromReflection(gbuffer, Resources("CameraParams"), SharedDrawPerDrawBindings.ResourceNames).Bindings), Is.EquivalentTo(new[] { 2u }));
		Assert.That(BindingRegisters(GraphicsPassBindingSet.FromReflection(shadow, Resources("CameraParams", "g_TerrainMaterialTable"), SharedDrawPerDrawBindings.ResourceNames).Bindings), Is.EquivalentTo(new[] { 16u, 14u }));
	}

	[Test]
	public void MissingReflectedPassResourceFailsValidation()
	{
		var reflection = new ShaderReflectionLayout([Constant("CameraParams", 2)]);
		Assert.That(() => GraphicsPassBindingSet.FromReflection(reflection,
			new Dictionary<string, IGfxBuffer?>(), SharedDrawPerDrawBindings.ResourceNames),
			Throws.InvalidOperationException.With.Message.Contains("CameraParams"));
	}

	private static ShaderConstantBufferLayout Constant(string name, uint register) =>
		new(name, register, 16, new Dictionary<string, ShaderConstantFieldLayout>());

	private static Dictionary<string, IGfxBuffer?> Resources(params string[] names)
	{
		var result = new Dictionary<string, IGfxBuffer?>();
		foreach (var name in names)
			result[name] = new TestBuffer(name);
		return result;
	}

	private static List<(GraphicsPassBindingKind, uint)> BindingSlots(ReadOnlySpan<GraphicsPassBinding> bindings)
	{
		var result = new List<(GraphicsPassBindingKind, uint)>(bindings.Length);
		foreach (var binding in bindings)
			result.Add((binding.Kind, binding.RegisterIndex));
		return result;
	}

	private static List<uint> BindingRegisters(ReadOnlySpan<GraphicsPassBinding> bindings)
	{
		var result = new List<uint>(bindings.Length);
		foreach (var binding in bindings)
			result.Add(binding.RegisterIndex);
		return result;
	}

	private sealed class TestBuffer(string name) : IGfxBuffer
	{
		public string? Name => name;
		public BufferDescriptor Descriptor => new(16, BufferUsage.Constant);
	}
}
