namespace WolfEngine.ECS;

/// <summary>Draws a section heading above a component field or data-asset property in the editor.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class HeaderAttribute(string text) : Attribute
{
	public string Text { get; } = text;
}

/// <summary>Adds vertical space, in ImGui points, before an inspector member and its heading.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class SpaceAttribute : Attribute
{
	public float Height { get; }

	public SpaceAttribute(float height = 8.0f)
	{
		if (float.IsFinite(height) == false || height < 0.0f)
		{
			throw new ArgumentOutOfRangeException(nameof(height), "Inspector spacing must be finite and non-negative.");
		}

		Height = height;
	}
}
