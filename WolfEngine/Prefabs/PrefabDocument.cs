namespace WolfEngine;

/// <summary>
/// The authored form of a prefab, as the editor saves it in a <c>.prefab.json</c> file. The build cooks it to the
/// binary format <see cref="Prefab.LoadCooked"/> reads. Authoring-only members of that file (icons, prefab links
/// and overrides) are not part of this contract and are ignored.
/// </summary>
public sealed class PrefabDocument
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid RootEntityId { get; set; }
	public List<EntityDocument> Entities { get; set; } = [];
}
