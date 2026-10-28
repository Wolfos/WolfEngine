using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

internal static class InspectorLayout
{
	// Weak keys let gameplay assemblies unload without retaining their types or members.
	private static readonly ConditionalWeakTable<Type, PropertyInfo[]> EditableProperties = new();
	private static readonly ConditionalWeakTable<MemberInfo, Decorations> MemberDecorations = new();

	public static IReadOnlyList<PropertyInfo> GetEditableProperties(Type type)
	{
		return EditableProperties.GetValue(type, static targetType => targetType
			.GetProperties(BindingFlags.Instance | BindingFlags.Public)
			.Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
			.OrderBy(property => GetInheritanceDepth(property.DeclaringType))
			.ThenBy(property => property.MetadataToken)
			.ToArray());
	}

	public static void DrawDecorations(MemberInfo member)
	{
		if (ImGui.GetCurrentContext() == IntPtr.Zero) return;

		var decorations = MemberDecorations.GetValue(member, static value => new Decorations(
			value.GetCustomAttribute<HeaderAttribute>()?.Text,
			value.GetCustomAttribute<SpaceAttribute>()?.Height ?? 0.0f));
		DrawSpace(decorations.Space);

		if (string.IsNullOrWhiteSpace(decorations.Header) == false)
		{
			ImGui.SeparatorText(decorations.Header);
		}
	}

	public static void DrawSpace(float height = 8.0f)
	{
		if (height <= 0.0f) return;

		ImGui.Dummy(new Vector2(0.0f, height));
	}

	private static int GetInheritanceDepth(Type? type)
	{
		var depth = 0;
		while (type is not null)
		{
			depth++;
			type = type.BaseType;
		}

		return depth;
	}

	private sealed record Decorations(string? Header, float Space);
}
