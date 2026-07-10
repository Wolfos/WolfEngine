#nullable enable

using System;
using System.Collections;
using System.Reflection;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

internal static class ShaderPipelineInvalidation
{
	public static void Invalidate(object target)
	{
		ArgumentNullException.ThrowIfNull(target);
		for (var type = target.GetType(); type is not null; type = type.BaseType)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (typeof(IGfxPipeline).IsAssignableFrom(field.FieldType))
				{
					field.SetValue(target, null);
					continue;
				}
				if (field.GetValue(target) is Array array && field.FieldType.GetElementType() is { } element &&
				    typeof(IGfxPipeline).IsAssignableFrom(element))
				{
					Array.Clear(array);
					continue;
				}
				if (field.GetValue(target) is IDictionary dictionary && IsPipelineDictionary(field.FieldType))
					dictionary.Clear();
			}
		}
	}

	private static bool IsPipelineDictionary(Type type)
	{
		if (type.IsGenericType == false) return false;
		var arguments = type.GetGenericArguments();
		return arguments.Length == 2 && typeof(IGfxPipeline).IsAssignableFrom(arguments[1]);
	}
}
