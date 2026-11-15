using System.Numerics;
using ImGuiNET;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[NonParallelizable]
public sealed class EditorFieldTests
{
	private nint _context;
	private string _text = "Idle";

	[SetUp]
	public void SetUp()
	{
		_context = ImGui.CreateContext();
		var io = ImGui.GetIO();
		io.DisplaySize = new Vector2(800.0f, 600.0f);
		io.DeltaTime = 1.0f / 60.0f;
		io.Fonts.GetTexDataAsRGBA32(out nint pixels, out var width, out var height);
		_text = "Idle";
	}

	[TearDown]
	public void TearDown()
	{
		ImGui.DestroyContext(_context);
	}

	[Test]
	public void TextEditingAndDeactivationRemainVisibleToTheCaller()
	{
		var bounds = (Min: Vector2.Zero, Max: Vector2.Zero);
		var active = false;
		var changed = false;
		var deactivated = false;
		var finishPosition = Vector2.Zero;
		void DrawFields()
		{
			changed = EditorUIUtility.InputText("Slot name", ref _text);
			bounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
			active = ImGui.IsItemActive();
			deactivated = ImGui.IsItemDeactivatedAfterEdit();
			ImGui.Button("Finish");
			finishPosition = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
		}

		DrawFrame(DrawFields);
		DrawFrame(DrawFields);
		Press(new Vector2(bounds.Min.X + 30.0f, bounds.Max.Y - 12.0f), DrawFields);
		ImGui.GetIO().AddInputCharactersUTF8("X");
		DrawFrame(DrawFields);

		Assert.Multiple(() =>
		{
			Assert.That(_text, Does.Contain("X"));
			Assert.That(changed, Is.True);
			Assert.That(active, Is.True);
		});

		ImGui.GetIO().MousePos = finishPosition;
		ImGui.GetIO().MouseDown[0] = true;
		DrawFrame(DrawFields);

		Assert.That(deactivated, Is.True);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void DisabledFieldsDoNotAcceptEdits(bool disabled)
	{
		var position = Vector2.Zero;
		var active = false;
		void DrawField()
		{
			ImGui.BeginDisabled(disabled);
			EditorUIUtility.InputText("Slot name", ref _text);
			position = new Vector2(ImGui.GetItemRectMin().X + 30.0f, ImGui.GetItemRectMax().Y - 12.0f);
			active = ImGui.IsItemActive();
			ImGui.EndDisabled();
		}

		DrawFrame(DrawField);
		DrawFrame(DrawField);
		Press(position, DrawField);
		ImGui.GetIO().AddInputCharactersUTF8("X");
		DrawFrame(DrawField);

		Assert.Multiple(() =>
		{
			Assert.That(active, Is.EqualTo(disabled == false));
			Assert.That(_text.Contains('X'), Is.EqualTo(disabled == false));
		});
	}

	[TestCase(false, EditorReferenceKind.Asset)]
	[TestCase(false, EditorReferenceKind.Entity)]
	[TestCase(true, EditorReferenceKind.Asset)]
	public void PickersKeepNativePopupStylesAndAllowSelection(bool combo, EditorReferenceKind referenceKind)
	{
		var selected = false;
		var opened = false;
		var popupStyleRestored = false;
		var fieldBounds = (Min: Vector2.Zero, Max: Vector2.Zero);
		var selectionPosition = Vector2.Zero;
		var style = ImGui.GetStyle();
		var padding = style.FramePadding;
		var background = style.Colors[(int)ImGuiCol.FrameBg];
		void DrawContents()
		{
			popupStyleRestored = ImGui.GetStyle().FramePadding == padding &&
				ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg] == background;

			if (ImGui.Selectable("Walking_A"))
			{
				selected = true;
				ImGui.CloseCurrentPopup();
			}

			selectionPosition = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
		}

		void DrawField()
		{
			opened = combo
				? EditorUIUtility.Combo("Clip", "Idle_A", DrawContents)
				: EditorUIUtility.PopupButton("Clip", "Idle_A", "Picker", new Vector2(240.0f, 120.0f), DrawContents, referenceKind: referenceKind);
			fieldBounds = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
		}

		DrawFrame(DrawField);
		DrawFrame(DrawField);
		Press(new Vector2(fieldBounds.Min.X + 30.0f, fieldBounds.Max.Y - 12.0f), DrawField);
		DrawFrame(DrawField);

		Assert.Multiple(() =>
		{
			Assert.That(opened, Is.True);
			Assert.That(popupStyleRestored, Is.True);
		});

		Press(selectionPosition, DrawField);

		Assert.That(selected, Is.True);
	}

	[TestCase(160.0f)]
	[TestCase(480.0f)]
	public void LongLabelsAndVectorFieldsStayWithinThePanel(float width)
	{
		var vector = Vector3.One;
		DrawFrame(() =>
		{
			var start = ImGui.GetCursorScreenPos();
			var availableWidth = ImGui.GetContentRegionAvail().X;
			EditorUIUtility.InputVector3("A long label that must not push the value outside the panel", ref vector);
			var fieldMax = ImGui.GetItemRectMax();
			var nextY = ImGui.GetCursorScreenPos().Y;

			Assert.Multiple(() =>
			{
				Assert.That(fieldMax.X, Is.LessThanOrEqualTo(start.X + availableWidth + 1.0f));
				Assert.That(nextY, Is.GreaterThanOrEqualTo(fieldMax.Y));
				Assert.That(vector, Is.EqualTo(Vector3.One));
			});
		}, width);
	}

	[TestCase(0)]
	[TestCase(1)]
	[TestCase(2)]
	public void EditingAVectorAxisPreservesTheOtherComponents(int axis)
	{
		var vector = new Vector3(1.0f, 2.0f, 3.0f);
		var position = Vector2.Zero;
		var changed = false;
		var active = false;
		void DrawField()
		{
			changed = EditorUIUtility.InputVector3("Position", ref vector);
			var min = ImGui.GetItemRectMin();
			var max = ImGui.GetItemRectMax();
			var cellWidth = (max.X - min.X - 12.0f) / 3.0f;
			position = new Vector2(min.X + axis * (cellWidth + 6.0f) + cellWidth * 0.6f, max.Y - 12.0f);
			active = ImGui.IsItemActive();
		}

		DrawFrame(DrawField);
		DrawFrame(DrawField);
		Press(position, DrawField);
		ImGui.GetIO().AddInputCharactersUTF8("42");
		DrawFrame(DrawField);
		var expected = new Vector3(1.0f, 2.0f, 3.0f);
		expected[axis] = 42.0f;

		Assert.Multiple(() =>
		{
			Assert.That(changed, Is.True);
			Assert.That(active, Is.True);
			Assert.That(vector, Is.EqualTo(expected));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ColorSwatchOpensAUsablePickerInNarrowAndWidePanels(bool narrow)
	{
		var color = Vector4.One;
		var position = Vector2.Zero;
		var pickerVisible = false;
		var width = narrow ? 180.0f : 480.0f;
		void DrawField()
		{
			EditorUIUtility.ColorEdit4("Color", ref color);
			var min = ImGui.GetItemRectMin();
			var max = ImGui.GetItemRectMax();
			position = new Vector2(narrow ? min.X + 10.0f : max.X - 10.0f, max.Y - 12.0f);
			pickerVisible = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
		}

		DrawFrame(DrawField, width);
		DrawFrame(DrawField, width);
		Press(position, DrawField, width);
		DrawFrame(DrawField, width);

		Assert.Multiple(() =>
		{
			Assert.That(pickerVisible, Is.True);
			Assert.That(color, Is.EqualTo(Vector4.One));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ComponentCardsResizeWhenCollapsedAndKeepTheirStatesIndependent(bool withRemoveButton)
	{
		var headerPosition = Vector2.Zero;
		var firstHeight = 0.0f;
		var secondHeight = 0.0f;
		var firstOpen = true;
		var secondOpen = true;
		var originalBackground = ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg];
		void DrawCards()
		{
			using (new EditorComponentSection(typeof(Vector2)))
			{
				firstOpen = EditorComponentSection.DrawHeader("First component", withRemoveButton ? 1 : 0, out var removeRequested);
				Assert.That(removeRequested, Is.False);
				headerPosition = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
				if (firstOpen)
				{
					EditorUIUtility.InputText("Name", ref _text);
					EditorUIUtility.InputText("Description", ref _text);
				}

				firstHeight = ImGui.GetWindowSize().Y;
			}

			using (new EditorComponentSection(typeof(Vector3)))
			{
				secondOpen = EditorComponentSection.DrawHeader("Second component", withRemoveButton ? 1 : 0, out _);
				if (secondOpen)
				{
					EditorUIUtility.InputText("Name", ref _text);
				}

				secondHeight = ImGui.GetWindowSize().Y;
			}

			Assert.That(ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg], Is.EqualTo(originalBackground));
		}

		DrawFrame(DrawCards);
		DrawFrame(DrawCards);
		DrawFrame(DrawCards);
		var expandedHeight = firstHeight;
		var otherHeight = secondHeight;
		Press(headerPosition, DrawCards);
		DrawFrame(DrawCards);
		DrawFrame(DrawCards);

		Assert.Multiple(() =>
		{
			Assert.That(firstOpen, Is.False);
			Assert.That(secondOpen, Is.True);
			Assert.That(firstHeight, Is.LessThan(expandedHeight));
			Assert.That(secondHeight, Is.EqualTo(otherHeight));
		});

		Press(headerPosition, DrawCards);
		DrawFrame(DrawCards);
		DrawFrame(DrawCards);

		Assert.Multiple(() =>
		{
			Assert.That(firstOpen, Is.True);
			Assert.That(firstHeight, Is.EqualTo(expandedHeight));
			Assert.That(secondOpen, Is.True);
			Assert.That(secondHeight, Is.EqualTo(otherHeight));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ComponentRemoveButtonDoesNotToggleTheHeader(bool collapsed)
	{
		var headerPosition = Vector2.Zero;
		var removePosition = Vector2.Zero;
		var open = true;
		var removeRequested = false;
		void DrawCard()
		{
			using var section = new EditorComponentSection(typeof(Vector2));
			open = EditorComponentSection.DrawHeader("Component", 1, out removeRequested);
			var min = ImGui.GetItemRectMin();
			var max = ImGui.GetItemRectMax();
			headerPosition = (min + max) * 0.5f;
			removePosition = new Vector2(max.X - (max.Y - min.Y) * 0.5f, headerPosition.Y);
			if (open)
			{
				EditorUIUtility.InputText("Name", ref _text);
			}
		}

		DrawFrame(DrawCard);
		DrawFrame(DrawCard);
		DrawFrame(DrawCard);
		if (collapsed)
		{
			Press(headerPosition, DrawCard);
			DrawFrame(DrawCard);
		}

		Press(removePosition, DrawCard);

		Assert.Multiple(() =>
		{
			Assert.That(removeRequested, Is.True);
			Assert.That(open, Is.EqualTo(collapsed == false));
		});
	}

	[Test]
	public void LargeComponentCardsKeepAllFieldsReachableAndForwardScrollingToTheInspector()
	{
		var bodyPosition = Vector2.Zero;
		var childHeight = 0.0f;
		var scroll = 0.0f;
		void DrawCard()
		{
			using (new EditorComponentSection(typeof(Vector2)))
			{
				EditorComponentSection.DrawHeader("Large component");
				bodyPosition = ImGui.GetCursorScreenPos() + new Vector2(30.0f, 20.0f);
				for (var index = 0; index < 40; index++)
				{
					ImGui.PushID(index);
					EditorUIUtility.InputText("Value", ref _text);
					ImGui.PopID();
				}

				childHeight = ImGui.GetWindowSize().Y;
			}

			scroll = ImGui.GetScrollY();
		}

		DrawFrame(DrawCard);
		DrawFrame(DrawCard);
		DrawFrame(DrawCard);
		ImGui.GetIO().MousePos = bodyPosition;
		DrawFrame(DrawCard);
		ImGui.GetIO().AddMouseWheelEvent(0.0f, -1.0f);
		DrawFrame(DrawCard);

		Assert.Multiple(() =>
		{
			Assert.That(childHeight, Is.GreaterThan(1500.0f));
			Assert.That(scroll, Is.GreaterThan(0.0f));
		});
	}

	private void Press(Vector2 position, Action draw, float width = 480.0f)
	{
		ImGui.GetIO().MousePos = position;
		DrawFrame(draw, width);
		ImGui.GetIO().MouseDown[0] = true;
		DrawFrame(draw, width);
		ImGui.GetIO().MouseDown[0] = false;
		DrawFrame(draw, width);
	}

	private static void DrawFrame(Action draw, float width = 480.0f)
	{
		ImGui.NewFrame();
		ImGui.SetNextWindowPos(new Vector2(10.0f, 10.0f));
		ImGui.SetNextWindowSize(new Vector2(width, 500.0f));
		ImGui.Begin("Field tests", ImGuiWindowFlags.NoSavedSettings);
		draw();
		ImGui.End();
		ImGui.Render();
	}
}
