namespace WolfEngine.Rendering;

/// <summary>
/// Recognises the bindless tables in a shader's reflection. The backend binds them itself, so a pass must neither
/// bind nor fill them; on Metal they reflect as argument buffers that look like constant buffers.
/// </summary>
internal static class BindlessReflection
{
	public static bool IsBindlessResource(string name, uint registerIndex)
	{
		return registerIndex == 27 ||
			name.Equals("BindlessCounts", StringComparison.Ordinal) ||
			name.Contains("Heap", StringComparison.Ordinal) ||
			name.StartsWith("g_Textures", StringComparison.Ordinal) ||
			name.StartsWith("g_RWTextures", StringComparison.Ordinal) ||
			name.StartsWith("g_Samplers", StringComparison.Ordinal);
	}
}
