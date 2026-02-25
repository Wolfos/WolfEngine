using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public class EntitiesWindow
{
	private static readonly List<Entity> AllEntities = new();
	private static readonly List<Entity> RootEntities = new();
	private static readonly Vector2 EntityIconSize = Vector2.One * 15.5f;

	public static void Draw(EditorScene scene, IIconManager icons)
	{
		var world = scene.World;

		ImGui.SetNextWindowPos(new Vector2(0.0f, 0.0f), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Vector2(188.0f, 720.0f), ImGuiCond.FirstUseEver);

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 3.0f));
		ImGui.Begin("Entities");
		ImGui.PopStyleVar();

		world.GetAllEntities(AllEntities);
		BuildRootList(world);

		var style = ImGui.GetStyle();
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, 0.0f));
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 4.0f));

		foreach (var entity in RootEntities)
		{
			DrawEntityNode(entity, world, scene, icons);
		}

		ImGui.PopStyleVar(2);
		ImGui.End();
	}

	private static void BuildRootList(World world)
	{
		RootEntities.Clear();
		foreach (var entity in AllEntities)
		{
			if (!world.HasComponent<Parent>(entity))
			{
				RootEntities.Add(entity);
				continue;
			}

			var parent = world.GetComponent<Parent>(entity).Value;
			if (!parent.IsValid)
			{
				RootEntities.Add(entity);
			}
		}
	}

	private static unsafe void DrawEntityNode(Entity entity, World world, EditorScene scene, IIconManager icons)
	{
		ImGui.PushID(entity.Index);

		var isSelected = EditorGui.HasSelectedEntity && EditorGui.SelectedEntity == entity;
		var hasChildren = world.HasComponent<Children>(entity)
		                 && world.GetComponent<Children>(entity).First.IsValid;
		var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.FramePadding;
		if (!hasChildren)
		{
			flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
		}

		if (isSelected)
		{
			flags |= ImGuiTreeNodeFlags.Selected;
			var selectedColor = ImGui.GetStyleColorVec4(ImGuiCol.HeaderActive);
			ImGui.PushStyleColor(ImGuiCol.Header, *selectedColor);
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, *selectedColor);
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, *selectedColor);
		}

		var iconName = scene.EntityIcons.TryGetValue(entity, out var assignedIconName)
			? assignedIconName
			: "object";
		var iconTexture = ResolveIconTexture(icons, iconName);

		var nameComponent = world.GetComponent<NameComponent>(entity);
		var name = nameComponent.Name ?? "Unnamed";
		var nodeCursorPosition = ImGui.GetCursorScreenPos();
		var open = ImGui.TreeNodeEx("##EntityNode", flags);
		var nodeClicked = ImGui.IsItemClicked();
		DrawEntityLabelWithIcon(name, iconTexture, nodeCursorPosition.X);

		if (nodeClicked)
		{
			SelectEntity(entity, world);
		}

		if (hasChildren && open)
		{
			var childEntity = world.GetComponent<Children>(entity).First;
			while (childEntity.IsValid)
			{
				DrawEntityNode(childEntity, world, scene, icons);

				if (world.HasComponent<Sibling>(childEntity))
				{
					childEntity = world.GetComponent<Sibling>(childEntity).Next;
				}
				else
				{
					break;
				}
			}

			ImGui.TreePop();
		}

		if (isSelected)
		{
			ImGui.PopStyleColor(3);
		}

		ImGui.PopID();
	}

	private static nint ResolveIconTexture(IIconManager icons, string iconName)
	{
		if (icons.TryGet(iconName, out var textureId))
		{
			return textureId;
		}

		if (icons.TryGet("object", out textureId))
		{
			return textureId;
		}

		return 0;
	}

	private static void DrawEntityLabelWithIcon(string label, nint iconTexture, float nodeCursorX)
	{
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var rowHeight = itemMax.Y - itemMin.Y;

		var labelStartX = nodeCursorX + ImGui.GetTreeNodeToLabelSpacing();
		var iconSize = MathF.Min(EntityIconSize.X, MathF.Max(1.0f, rowHeight - 2.0f));
		var iconPosition = new Vector2(labelStartX, itemMin.Y + (rowHeight - iconSize) * 0.5f);
		var textSize = ImGui.CalcTextSize(label);
		var textPosition = new Vector2(
			iconPosition.X + iconSize + 4.0f,
			itemMin.Y + (rowHeight - textSize.Y) * 0.5f);
		var drawList = ImGui.GetWindowDrawList();

		if (iconTexture != 0)
		{
			drawList.AddImage(iconTexture, iconPosition, iconPosition + Vector2.One * iconSize);
		}

		drawList.AddText(textPosition, ImGui.GetColorU32(ImGuiCol.Text), label);
	}

	private static void SelectEntity(Entity entity, World world)
	{
		EditorGui.HasSelectedEntity = true;
		EditorGui.SelectedEntity = entity;
		world.GetComponentTypes(entity, EditorGui.SelectedComponentTypes);
	}
}
