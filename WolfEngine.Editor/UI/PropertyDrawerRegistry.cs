using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;


public readonly record struct PropertyDrawerContext(
	string Label,
	Type ValueType,
	object? Value,
	EditorScene? Scene = null,
	Entity? OwnerEntity = null,
	MemberInfo? Member = null);

public readonly record struct PropertyDrawerResult(bool Handled, bool Changed, object? Value);

public interface IPropertyDrawerRegistry
{
	PropertyDrawerResult Draw(PropertyDrawerContext context);
}

public sealed class PropertyDrawerRegistry : IPropertyDrawerRegistry
{
	private const string AssetLinkPickerPopupId = "AssetLinkPickerPopup";
	private static readonly Vector2 AssetLinkPickerSize = new(420.0f, 320.0f);

	private readonly IEditorProjectService _projectService;
	private readonly IProjectTypeResolver _typeResolver;
	private readonly Dictionary<uint, string> _assetLinkSearchTexts = new();

	public PropertyDrawerRegistry(IEditorProjectService projectService, IProjectTypeResolver typeResolver)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
	}

	public PropertyDrawerResult Draw(PropertyDrawerContext context)
	{
		var valueType = context.ValueType;
		var value = context.Value;

		if (TryDrawEntityLink(context, out var entityLinkResult))
		{
			return entityLinkResult;
		}

		if (TryDrawAssetLink(context, out var assetLinkResult))
		{
			return assetLinkResult;
		}

		if (valueType == typeof(float[]))
		{
			return DrawFloatArray(context.Label, value as float[]);
		}

		if (valueType == typeof(string))
		{
			var stringValue = (string?)value ?? string.Empty;
			var changed = EditorUIUtility.InputText(context.Label, ref stringValue);
			return new PropertyDrawerResult(true, changed, stringValue);
		}

		if (valueType == typeof(bool))
		{
			var boolValue = value is bool typedValue && typedValue;
			var changed = EditorUIUtility.Checkbox(context.Label, ref boolValue);
			return new PropertyDrawerResult(true, changed, boolValue);
		}

		if (valueType == typeof(int))
		{
			var intValue = value is int typedValue ? typedValue : 0;
			var changed = EditorUIUtility.InputInt(context.Label, ref intValue);
			return new PropertyDrawerResult(true, changed, intValue);
		}

		if (valueType == typeof(float))
		{
			var floatValue = value is float typedValue ? typedValue : 0.0f;
			var changed = EditorUIUtility.InputFloat(context.Label, ref floatValue);
			return new PropertyDrawerResult(true, changed, floatValue);
		}

		if (valueType == typeof(double))
		{
			var doubleValue = value is double typedValue ? typedValue : 0.0;
			var changed = EditorUIUtility.InputDouble(context.Label, ref doubleValue);
			return new PropertyDrawerResult(true, changed, doubleValue);
		}

		if (IsTextBackedNumericType(valueType))
		{
			var textValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
			var changed = EditorUIUtility.InputText(context.Label, ref textValue);
			if (changed == false)
			{
				return new PropertyDrawerResult(true, false, value);
			}

			if (TryConvertNumericValue(textValue, valueType, out var numericValue))
			{
				return new PropertyDrawerResult(true, true, numericValue);
			}

			return new PropertyDrawerResult(true, false, value);
		}

		if (valueType == typeof(Vector2))
		{
			var vectorValue = value is Vector2 typedValue ? typedValue : Vector2.Zero;
			var changed = EditorUIUtility.InputVector2(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, vectorValue);
		}

		if (valueType == typeof(Vector3))
		{
			var vectorValue = value is Vector3 typedValue ? typedValue : Vector3.Zero;
			var changed = EditorUIUtility.InputVector3(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, vectorValue);
		}

		if (valueType == typeof(Vector4))
		{
			var vectorValue = value is Vector4 typedValue ? typedValue : Vector4.Zero;
			var changed = EditorUIUtility.InputVector4(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, vectorValue);
		}

		if (valueType == typeof(Quaternion))
		{
			var quaternionValue = value is Quaternion typedValue ? typedValue : Quaternion.Identity;
			var vectorValue = new Vector4(quaternionValue.X, quaternionValue.Y, quaternionValue.Z, quaternionValue.W);
			var changed = EditorUIUtility.InputVector4(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, new Quaternion(vectorValue.X, vectorValue.Y, vectorValue.Z, vectorValue.W));
		}

		if (valueType == typeof(ColorRGBA))
		{
			var colorValue = (ColorRGBA)(value ?? ColorRGBA.White);
			var vectorValue = colorValue.ToVector4();
			var changed = EditorUIUtility.ColorEdit4(context.Label, ref vectorValue);
			return new PropertyDrawerResult(true, changed, ColorRGBA.FromVector4(vectorValue));
		}

		if (valueType.IsEnum)
		{
			return DrawEnum(context.Label, valueType, value);
		}

		return new PropertyDrawerResult(false, false, value);
	}

	private bool TryDrawAssetLink(PropertyDrawerContext context, out PropertyDrawerResult result)
	{
		var valueType = context.ValueType;
		if (valueType.IsGenericType == false || valueType.GetGenericTypeDefinition() != typeof(AssetRef<>))
		{
			result = default;
			return false;
		}

		var assetType = valueType.GetGenericArguments()[0];
		if (TryGetRuntimeAssetDescriptor(assetType, out var descriptor) == false)
		{
			result = new PropertyDrawerResult(false, false, context.Value);
			return true;
		}

		var currentId = GetAssetLinkId(valueType, context.Value);
		var authoringTypeName = descriptor.AuthoringType.AssemblyQualifiedName ?? string.Empty;
		var authoringTypeId = _typeResolver.GetStableTypeId(descriptor.AuthoringType);
		var candidates = _projectService.HasOpenProject
			? AssetLinkPickerLogic.GetCandidates(_projectService.CurrentAssetDatabase.Assets, descriptor, authoringTypeName, authoringTypeId)
			: [];
		var currentAsset = _projectService.HasOpenProject && _projectService.TryGetAsset(currentId, out var asset)
			? asset
			: null;

		var nextId = currentId;
		var changed = false;
		EditorUIUtility.PopupButton(
			context.Label,
			AssetLinkPickerLogic.GetPreviewLabel(_projectService.HasOpenProject, currentId, currentAsset, descriptor, authoringTypeName, authoringTypeId),
			AssetLinkPickerPopupId,
			AssetLinkPickerSize,
			() =>
		{
			var popupStateId = ImGui.GetID(AssetLinkPickerPopupId);
			if (_assetLinkSearchTexts.TryGetValue(popupStateId, out var searchText) == false || ImGui.IsWindowAppearing())
			{
				searchText = string.Empty;
			}

			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.InputText("##AssetSearch", ref searchText, 256);
			_assetLinkSearchTexts[popupStateId] = searchText;
			ImGui.Separator();
			ImGui.BeginChild("AssetLinkResults", new Vector2(0.0f, 240.0f), ImGuiChildFlags.Borders);
			try
			{
				var noneSelected = currentId == Guid.Empty;
				ImGui.PushID("None");
				try
				{
					if (ImGui.Selectable("None", noneSelected))
					{
						nextId = Guid.Empty;
						changed = currentId != Guid.Empty;
						ImGui.CloseCurrentPopup();
					}

					if (noneSelected)
					{
						ImGui.SetItemDefaultFocus();
						if (ImGui.IsWindowAppearing())
						{
							ImGui.SetScrollHereY();
						}
					}
				}
				finally
				{
					ImGui.PopID();
				}

				var filteredCandidateCount = 0;
				for (var i = 0; i < candidates.Count; i++)
				{
					var candidate = candidates[i];
					if (AssetLinkPickerLogic.MatchesSearch(candidate.Name, searchText) == false)
					{
						continue;
					}

					filteredCandidateCount++;
					var isSelected = candidate.Id == currentId;
					ImGui.PushID(candidate.Id.ToString());
					try
					{
						if (ImGui.Selectable(candidate.Name, isSelected))
						{
							nextId = candidate.Id;
							changed = candidate.Id != currentId;
							ImGui.CloseCurrentPopup();
						}

						if (isSelected)
						{
							ImGui.SetItemDefaultFocus();
							if (ImGui.IsWindowAppearing())
							{
								ImGui.SetScrollHereY();
							}
						}
					}
					finally
					{
						ImGui.PopID();
					}
				}

				if (filteredCandidateCount == 0)
				{
					ImGui.TextDisabled("No matching assets.");
				}
			}
			finally
			{
				ImGui.EndChild();
			}
		});

		result = changed
			? new PropertyDrawerResult(true, true, CreateAssetLinkValue(valueType, nextId))
			: new PropertyDrawerResult(true, false, context.Value);
		return true;
	}

	private bool TryDrawEntityLink(PropertyDrawerContext context, out PropertyDrawerResult result)
	{
		if (context.ValueType != typeof(Entity) || context.Scene is null)
		{
			result = default;
			return false;
		}

		var scene = context.Scene;
		var ownerEntity = context.OwnerEntity;
		var requiredComponentType = GetRequiredComponentType(context.Member);
		var currentEntity = context.Value is Entity typedEntity ? typedEntity : default;
		var candidates = EntityLinkPickerLogic.GetCandidates(scene, ownerEntity, requiredComponentType);
		var currentEntityId = EntityLinkPickerLogic.TryGetPersistentEntityId(scene, currentEntity);
		var previewLabel = EntityLinkPickerLogic.GetPreviewLabel(scene, currentEntity, currentEntityId);
		var popupId = $"EntityLinkPickerPopup##{context.Label}";
		var nextEntity = currentEntity;
		var changed = false;
		EditorUIUtility.PopupButton(context.Label, previewLabel, popupId, AssetLinkPickerSize, () =>
		{
			var popupStateId = ImGui.GetID(popupId);
			if (_assetLinkSearchTexts.TryGetValue(popupStateId, out var searchText) == false || ImGui.IsWindowAppearing())
			{
				searchText = string.Empty;
			}

			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.InputText("##EntitySearch", ref searchText, 256);
			_assetLinkSearchTexts[popupStateId] = searchText;
			ImGui.Separator();
			ImGui.BeginChild("EntityLinkResults", new Vector2(0.0f, 240.0f), ImGuiChildFlags.Borders);
			try
			{
				var noneSelected = currentEntity.IsValid == false;
				ImGui.PushID("None");
				try
				{
					if (ImGui.Selectable("None", noneSelected))
					{
						nextEntity = default;
						changed = currentEntity.IsValid;
						ImGui.CloseCurrentPopup();
					}

					if (noneSelected)
					{
						ImGui.SetItemDefaultFocus();
						if (ImGui.IsWindowAppearing())
						{
							ImGui.SetScrollHereY();
						}
					}
				}
				finally
				{
					ImGui.PopID();
				}

				var filteredCandidateCount = 0;
				for (var i = 0; i < candidates.Count; i++)
				{
					var candidate = candidates[i];
					if (AssetLinkPickerLogic.MatchesSearch(candidate.DisplayName, searchText) == false)
					{
						continue;
					}

					filteredCandidateCount++;
					var isSelected = currentEntityId is { } selectedId && selectedId == candidate.Id;
					ImGui.PushID(candidate.Id.ToString());
					try
					{
						if (ImGui.Selectable(candidate.DisplayName, isSelected))
						{
							nextEntity = candidate.Entity;
							changed = nextEntity != currentEntity;
							ImGui.CloseCurrentPopup();
						}

						if (isSelected)
						{
							ImGui.SetItemDefaultFocus();
							if (ImGui.IsWindowAppearing())
							{
								ImGui.SetScrollHereY();
							}
						}
					}
					finally
					{
						ImGui.PopID();
					}
				}

				if (filteredCandidateCount == 0)
				{
					ImGui.TextDisabled("No matching entities.");
				}
			}
			finally
			{
				ImGui.EndChild();
			}
		});

		result = changed
			? new PropertyDrawerResult(true, true, nextEntity)
			: new PropertyDrawerResult(true, false, context.Value);
		return true;
	}

	private static Type? GetRequiredComponentType(MemberInfo? member)
	{
		if (member is null)
		{
			return null;
		}

		return member.GetCustomAttribute<RequireComponentAttribute>()?.Type;
	}

	private static PropertyDrawerResult DrawEnum(string label, Type enumType, object? value)
	{
		var changed = false;
		var nextValue = value;
		var preview = value?.ToString() ?? string.Empty;
		EditorUIUtility.Combo(label, preview, () =>
		{
			foreach (var candidate in Enum.GetValues(enumType))
			{
				var candidateName = candidate?.ToString() ?? string.Empty;
				var isSelected = Equals(candidate, value);
				if (ImGui.Selectable(candidateName, isSelected))
				{
					nextValue = candidate;
					changed = true;
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		return new PropertyDrawerResult(true, changed, nextValue);
	}

	private static bool IsTextBackedNumericType(Type valueType)
	{
		return valueType == typeof(long) ||
		       valueType == typeof(uint) ||
		       valueType == typeof(ulong) ||
		       valueType == typeof(short) ||
		       valueType == typeof(ushort) ||
		       valueType == typeof(byte) ||
		       valueType == typeof(sbyte) ||
		       valueType == typeof(decimal);
	}

	private static bool TryConvertNumericValue(string textValue, Type valueType, out object? numericValue)
	{
		var culture = CultureInfo.InvariantCulture;
		if (valueType == typeof(long) && long.TryParse(textValue, culture, out var longValue))
		{
			numericValue = longValue;
			return true;
		}

		if (valueType == typeof(uint) && uint.TryParse(textValue, culture, out var uintValue))
		{
			numericValue = uintValue;
			return true;
		}

		if (valueType == typeof(ulong) && ulong.TryParse(textValue, culture, out var ulongValue))
		{
			numericValue = ulongValue;
			return true;
		}

		if (valueType == typeof(short) && short.TryParse(textValue, culture, out var shortValue))
		{
			numericValue = shortValue;
			return true;
		}

		if (valueType == typeof(ushort) && ushort.TryParse(textValue, culture, out var ushortValue))
		{
			numericValue = ushortValue;
			return true;
		}

		if (valueType == typeof(byte) && byte.TryParse(textValue, culture, out var byteValue))
		{
			numericValue = byteValue;
			return true;
		}

		if (valueType == typeof(sbyte) && sbyte.TryParse(textValue, culture, out var sbyteValue))
		{
			numericValue = sbyteValue;
			return true;
		}

		if (valueType == typeof(decimal) && decimal.TryParse(textValue, culture, out var decimalValue))
		{
			numericValue = decimalValue;
			return true;
		}

		numericValue = null;
		return false;
	}

	private static PropertyDrawerResult DrawFloatArray(string label, float[]? value)
	{
		var current = value ?? Array.Empty<float>();
		var next = (float[])current.Clone();
		var changed = false;

		ImGui.PushID(label);
		try
		{
			ImGui.TextUnformatted(label);
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				var count = next.Length;
				if (EditorUIUtility.InputInt("Count", ref count))
				{
					count = Math.Clamp(count, 0, 32);
					if (count != next.Length)
					{
						Array.Resize(ref next, count);
						changed = true;
					}
				}

				for (var i = 0; i < next.Length; i++)
				{
					var item = next[i];
					if (EditorUIUtility.InputFloat($"[{i}]", ref item))
					{
						next[i] = item;
						changed = true;
					}
				}
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}
		finally
		{
			ImGui.PopID();
		}

		return new PropertyDrawerResult(true, changed, next);
	}

	private static bool TryGetRuntimeAssetDescriptor(Type runtimeType, out RuntimeAssetAttribute descriptor)
	{
		try
		{
			descriptor = RuntimeAssetDescriptor.Get(runtimeType);
			return true;
		}
		catch
		{
			descriptor = null!;
			return false;
		}
	}

	internal static Guid GetAssetLinkId(Type valueType, object? value)
	{
		if (value is null)
		{
			return Guid.Empty;
		}

		var nodeIdProperty = valueType.GetProperty(nameof(AssetRef<IDataAsset>.NodeId))
			?? throw new InvalidOperationException($"Asset reference type '{valueType.FullName}' is missing its NodeId property.");
		return nodeIdProperty.GetValue(value) is Guid nodeId ? nodeId : Guid.Empty;
	}

	internal static object CreateAssetLinkValue(Type valueType, Guid assetId)
	{
		var boxedValue = Activator.CreateInstance(valueType)
			?? throw new InvalidOperationException($"Failed to create asset reference value for '{valueType.FullName}'.");
		var nodeIdProperty = valueType.GetProperty(nameof(AssetRef<IDataAsset>.NodeId))
			?? throw new InvalidOperationException($"Asset reference type '{valueType.FullName}' is missing its NodeId property.");
		nodeIdProperty.SetValue(boxedValue, assetId);
		return boxedValue;
	}
}

internal readonly record struct EntityLinkCandidate(Guid Id, Entity Entity, string DisplayName);

internal static class EntityLinkPickerLogic
{
	public static List<EntityLinkCandidate> GetCandidates(EditorScene scene, Entity? ownerEntity, Type? requiredComponentType = null)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var candidates = new List<EntityLinkCandidate>();
		foreach (var entry in scene.EntityIds)
		{
			if (entry.Value == Guid.Empty ||
			    scene.World.IsAlive(entry.Key) == false ||
			    (requiredComponentType is not null && scene.World.HasComponent(entry.Key, requiredComponentType) == false))
			{
				continue;
			}

			candidates.Add(new EntityLinkCandidate(entry.Value, entry.Key, GetDisplayName(scene, entry.Key, entry.Value)));
		}

		candidates.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName));
		return candidates;
	}

	public static Guid? TryGetPersistentEntityId(EditorScene scene, Entity entity)
	{
		ArgumentNullException.ThrowIfNull(scene);
		if (entity.IsValid == false || scene.World.IsAlive(entity) == false)
		{
			return null;
		}

		return scene.EntityIds.TryGetValue(entity, out var entityId) && entityId != Guid.Empty
			? entityId
			: null;
	}

	public static string GetPreviewLabel(EditorScene scene, Entity entity, Guid? entityId)
	{
		ArgumentNullException.ThrowIfNull(scene);
		if (entity.IsValid == false)
		{
			return "None";
		}

		if (entityId is not { } resolvedEntityId || resolvedEntityId == Guid.Empty || scene.World.IsAlive(entity) == false)
		{
			return "Missing";
		}

		return GetDisplayName(scene, entity, resolvedEntityId);
	}

	private static string GetDisplayName(EditorScene scene, Entity entity, Guid entityId)
	{
		ArgumentNullException.ThrowIfNull(scene);

		if (scene.World.HasComponent<NameComponent>(entity))
		{
			var name = scene.World.GetComponent<NameComponent>(entity).Name;
			if (string.IsNullOrWhiteSpace(name) == false)
			{
				return name;
			}
		}

		var shortId = entityId.ToString("N")[..8];
		return $"Entity {shortId}";
	}
}

internal static class AssetLinkPickerLogic
{
	public static List<AssetDatabaseEntry> GetCandidates(
		IReadOnlyList<AssetDatabaseEntry> assets,
		RuntimeAssetAttribute descriptor,
		string authoringTypeName,
		string authoringTypeId)
	{
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentNullException.ThrowIfNull(descriptor);

		return assets
			.Where(asset => IsMatchingCandidate(asset, descriptor, authoringTypeName, authoringTypeId))
			.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public static string GetPreviewLabel(
		bool hasOpenProject,
		Guid assetId,
		AssetDatabaseEntry? asset,
		RuntimeAssetAttribute descriptor,
		string authoringTypeName,
		string authoringTypeId)
	{
		if (assetId == Guid.Empty)
		{
			return "None";
		}

		if (hasOpenProject == false || asset is null)
		{
			return "Missing";
		}

		return IsMatchingCandidate(asset, descriptor, authoringTypeName, authoringTypeId)
			? asset.Name
			: "Invalid";
	}

	public static bool MatchesSearch(string assetName, string searchText)
	{
		ArgumentNullException.ThrowIfNull(assetName);

		return string.IsNullOrWhiteSpace(searchText) ||
		       assetName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsMatchingCandidate(
		AssetDatabaseEntry asset,
		RuntimeAssetAttribute descriptor,
		string authoringTypeName,
		string authoringTypeId)
	{
		ArgumentNullException.ThrowIfNull(asset);
		ArgumentNullException.ThrowIfNull(descriptor);

		if (asset.Type != descriptor.AssetType)
		{
			return false;
		}

		if (descriptor.AssetType != AssetType.DataAsset)
		{
			return true;
		}

		return asset.TryGetSummary<DataAssetSummary>(out var summary) &&
		       (string.Equals(summary.DataAssetTypeId, authoringTypeId, StringComparison.Ordinal) ||
		        string.Equals(summary.DataAssetType, authoringTypeName, StringComparison.Ordinal));
	}
}
