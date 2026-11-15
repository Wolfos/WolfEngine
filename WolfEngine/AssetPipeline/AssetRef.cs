using System.Text.Json.Serialization;

namespace WolfEngine.AssetPipeline;

public struct AssetRef<T>
{
	public Guid NodeId { get; set; }

	[JsonIgnore]
	public T? Asset => AssetDatabase.GetInstance<T>(NodeId);
	[JsonIgnore]
	public bool IsValid => NodeId != Guid.Empty;

	/// <summary>
	/// <c>asset:</c> followed by the node id, or an empty string for an unset reference. Text is the only form some
	/// consumers can carry, such as a Razor attribute on an HTML element, which Blazor always converts to a string.
	/// </summary>
	public override string ToString()
	{
		return AssetReferenceText.Format(NodeId);
	}
}

/// <summary>The text form of an <see cref="AssetRef{T}"/>: <c>asset:</c> followed by the node id.</summary>
public static class AssetReferenceText
{
	public const string Prefix = "asset:";

	public static string Format(Guid nodeId)
	{
		if (nodeId == Guid.Empty)
		{
			return string.Empty;
		}

		return Prefix + nodeId.ToString("D");
	}

	public static bool TryParse(string? text, out Guid nodeId)
	{
		nodeId = Guid.Empty;

		if (text is null || text.StartsWith(Prefix, StringComparison.Ordinal) == false)
		{
			return false;
		}

		return Guid.TryParseExact(text.AsSpan(Prefix.Length), "D", out nodeId) &&
		       nodeId != Guid.Empty;
	}
}
