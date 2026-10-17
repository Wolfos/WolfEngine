using System.Reflection;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class FrameSnapshotConfigTests
{
	/// <summary>
	/// SetConfig copies RenderConfig property by property, so a property added without a matching copy is silently
	/// ignored by the renderer. Every property is changed from its default here and must reach the snapshot.
	/// </summary>
	[Test]
	public void SetConfigCopiesEveryRenderConfigProperty()
	{
		var source = new RenderConfig();
		var properties = typeof(RenderConfig)
			.GetProperties(BindingFlags.Instance | BindingFlags.Public)
			.Where(property => property.CanRead && property.CanWrite && property.SetMethod!.IsPublic)
			.ToArray();
		foreach (var property in properties)
		{
			var changed = ChangeValue(property.GetValue(source)!, property.PropertyType);
			Assert.That(changed, Is.Not.Null, $"No value of {property.Name} could be changed for the test.");
			property.SetValue(source, changed);
		}

		var snapshot = new FrameSnapshot();
		snapshot.SetConfig(source);

		foreach (var property in properties)
		{
			Assert.That(
				property.GetValue(snapshot.Config),
				Is.EqualTo(property.GetValue(source)),
				$"FrameSnapshot.SetConfig does not copy RenderConfig.{property.Name}.");
		}
	}

	/// <summary>Returns <paramref name="value"/> with one setting changed, or null when nothing could be.</summary>
	private static object? ChangeValue(object value, Type type)
	{
		if (type == typeof(bool))
		{
			return (bool)value == false;
		}

		if (type == typeof(float))
		{
			return (float)value == 0.5f ? 0.75f : 0.5f;
		}

		if (type == typeof(int))
		{
			return (int)value + 1;
		}

		if (type.IsEnum)
		{
			var values = Enum.GetValues(type);
			return values.Length > 1 ? values.GetValue((Array.IndexOf(values, value) + 1) % values.Length) : null;
		}

		if (type.IsValueType == false || type.IsPrimitive)
		{
			return null;
		}

		// A settings struct: change the first nested value that can be changed.
		var boxed = value;
		foreach (var nested in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (nested.CanRead == false || nested.CanWrite == false || nested.SetMethod!.IsPublic == false)
			{
				continue;
			}

			var changed = ChangeValue(nested.GetValue(boxed)!, nested.PropertyType);
			if (changed is null)
			{
				continue;
			}

			nested.SetValue(boxed, changed);

			return boxed;
		}

		return null;
	}
}
