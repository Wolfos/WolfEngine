using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace WolfEngine.UI.Tests;

public sealed class TextRunTests
{
	public sealed class Fragments : ComponentBase
	{
		[Parameter] public string Mode { get; set; } = "mixed";
		[Parameter] public string Value { get; set; } = "B";
		protected override void BuildRenderTree(RenderTreeBuilder b)
		{
			b.OpenElement(0, "div");
			switch (Mode)
			{
				case "mixed":
					b.AddContent(1, "A");
					b.OpenRegion(2); b.AddContent(0, " "); b.AddContent(1, Value); b.CloseRegion();
					b.AddMarkupContent(3, " &amp; "); b.AddContent(4, "<C>&amp;");
					break;
				case "boundaries":
					b.AddContent(1, "A"); b.AddMarkupContent(2, "<span>B</span> <span>C</span>"); b.AddContent(3, "D");
					break;
				case "newlines":
					b.AddContent(1, "first"); b.AddContent(2, "\n\n"); b.AddContent(3, "second");
					break;
				case "many":
					for (var i = 0; i < 500; i++) b.AddContent(1, i.ToString("D4", CultureInfo.InvariantCulture));
					break;
			}
			b.CloseElement();
		}
	}
	[TestCase("mixed", "A B & <C>&amp;")]
	[TestCase("newlines", "first\n\nsecond")]
	public void AdjacentTextMarkupAndRegionFragmentsShareOneRun(string mode, string expected)
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		using var surface = host.Create<Fragments>(new(), initialParameters: new Dictionary<string, object?> { [nameof(Fragments.Mode)] = mode });
		var children = ((GameplayUiSurface)surface).Root!.Children.Single().Children;
		Assert.That(children, Has.Count.EqualTo(1));
		Assert.That(children[0].Text, Is.EqualTo(expected));
	}
	[Test]
	public void ChildElementsRemainSeparateAndIndentationDoesNotCreateLayoutNodes()
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		using var surface = host.Create<Fragments>(new(), initialParameters: new Dictionary<string, object?> { [nameof(Fragments.Mode)] = "boundaries" });
		var children = ((GameplayUiSurface)surface).Root!.Children.Single().Children;
		Assert.That(children.Select(n => n.Name), Is.EqualTo(new[] { "#text", "span", "span", "#text" }));
		Assert.That(children[0].Text, Is.EqualTo("A")); Assert.That(children[^1].Text, Is.EqualTo("D"));
	}
	[Test]
	public void UpdatingOrEmptyingAnExpressionRetainsTheSingleTextNode()
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		using var surface = host.Create<Fragments>(new());
		var original = ((GameplayUiSurface)surface).Root!.Children.Single().Children.Single();
		foreach (var value in new[] { "123", "", " " })
		{
			surface.SetParameters(new Dictionary<string, object?> { [nameof(Fragments.Value)] = value });
			var current = ((GameplayUiSurface)surface).Root!.Children.Single().Children.Single();
			Assert.That(current, Is.SameAs(original));
			Assert.That(current.Text, Is.EqualTo($"A {value} & <C>&amp;"));
		}
	}
	[Test]
	public void HundredsOfFragmentsProduceOneLayoutNode()
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		using var surface = host.Create<Fragments>(new(), initialParameters: new Dictionary<string, object?> { [nameof(Fragments.Mode)] = "many" });
		var children = ((GameplayUiSurface)surface).Root!.Children.Single().Children;
		Assert.That(children, Has.Count.EqualTo(1));
		Assert.That(children[0].Text, Is.EqualTo(string.Concat(Enumerable.Range(0, 500).Select(i => i.ToString("D4", CultureInfo.InvariantCulture)))));
	}
}
