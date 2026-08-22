namespace WolfEngine.Rendering.Passes;

/// <summary>Reflected bindings which vary for every encoded indirect draw.</summary>
public readonly struct SharedDrawPerDrawBindings
{
	public static readonly IReadOnlySet<string> ResourceNames = new HashSet<string>(StringComparer.Ordinal)
	{
		"g_InstanceTable", "g_MaterialTable", "g_DrawArgsTable", "g_MaterialGenerations"
	};

	public SharedDrawPerDrawBindings(uint instanceRegisterIndex, uint materialRegisterIndex,
		uint drawArgsRegisterIndex, uint materialGenerationRegisterIndex)
	{
		InstanceRegisterIndex = instanceRegisterIndex;
		MaterialRegisterIndex = materialRegisterIndex;
		DrawArgsRegisterIndex = drawArgsRegisterIndex;
		MaterialGenerationRegisterIndex = materialGenerationRegisterIndex;
	}

	public uint InstanceRegisterIndex { get; }
	public uint MaterialRegisterIndex { get; }
	public uint DrawArgsRegisterIndex { get; }
	public uint MaterialGenerationRegisterIndex { get; }

	public static SharedDrawPerDrawBindings FromReflection(ShaderReflectionLayout reflection)
	{
		ArgumentNullException.ThrowIfNull(reflection);
		return new SharedDrawPerDrawBindings(reflection.GetResource("g_InstanceTable").RegisterIndex,
			reflection.GetResource("g_MaterialTable").RegisterIndex,
			reflection.GetResource("g_DrawArgsTable").RegisterIndex,
			reflection.GetResource("g_MaterialGenerations").RegisterIndex);
	}
}
