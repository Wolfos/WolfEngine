using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

public sealed class AnimationGraphSelectionTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void BoxSelectsIntersectingNodesInEitherDirectionWithoutEditingGraph(bool reverse)
	{
		var graph = Graph();
		var selection = new AnimationGraphSelection();
		var positions = Positions(graph);
		var start = new Vector2(220.0f, 40.0f);
		var end = new Vector2(240.0f, 180.0f);
		selection.SelectBox(graph, positions, reverse ? end : start, reverse ? start : end, []);

		Assert.That(selection.Ids, Is.EqualTo(graph.Nodes.Take(2).Select(node => node.Id)));
		Assert.That(selection.Primary, Is.EqualTo(graph.Nodes[0].Id));
		Assert.That(graph.Layout, Is.Empty);
		Assert.That(graph.Nodes.SelectMany(node => node.Inputs), Is.EqualTo(new[] { graph.Nodes[0].Id }));
	}

	[Test]
	public void AdditiveBoxKeepsTheFirstSelectedNodeAsInspectorTargetAndDoesNotKeepOldBoxHits()
	{
		var graph = Graph();
		var selection = new AnimationGraphSelection();
		var positions = Positions(graph);
		var initial = new[] { graph.Nodes[2].Id };
		selection.SelectBox(graph, positions, Vector2.Zero, new Vector2(240.0f, 180.0f), initial);
		Assert.That(selection.Ids, Is.EqualTo(new[] { graph.Nodes[2].Id, graph.Nodes[0].Id, graph.Nodes[1].Id }));

		selection.SelectBox(graph, positions, Vector2.Zero, new Vector2(20.0f), initial);
		Assert.That(selection.Ids, Is.EqualTo(new[] { graph.Nodes[2].Id, graph.Nodes[0].Id }));
		Assert.That(selection.Primary, Is.EqualTo(graph.Nodes[2].Id));
	}

	[Test]
	public void TogglingOrRemovingThePrimaryAdvancesToTheNextSelectedNode()
	{
		var graph = Graph();
		var selection = new AnimationGraphSelection();
		selection.Set(graph.Nodes.Select(node => node.Id));
		selection.Toggle(graph.Nodes[0].Id);
		Assert.That(selection.Primary, Is.EqualTo(graph.Nodes[1].Id));

		graph.Nodes.RemoveAt(1);
		selection.Prune(graph);
		Assert.That(selection.Ids, Is.EqualTo(new[] { graph.Nodes[1].Id }));
	}

	[Test]
	public void GroupDeletionRemovesLayoutsAndConnectionsToEveryDeletedNode()
	{
		var graph = Graph();
		foreach (var node in graph.Nodes)
		{
			graph.Layout.Add(new AnimationNodeLayout
			{
				NodeId = node.Id
			});
		}

		var removed = graph.Nodes.Take(2).Select(node => node.Id).ToHashSet();
		AnimationWindow.DeleteNodes(graph, removed);
		Assert.That(graph.Nodes, Has.Count.EqualTo(1));
		Assert.That(graph.Layout, Has.Count.EqualTo(1));
		Assert.That(graph.Nodes[0].Inputs, Is.EqualTo(new[] { Guid.Empty }));
	}

	private static AnimationGraph Graph()
	{
		var first = new AnimationNode
		{
			Id = Guid.NewGuid(),
			Kind = AnimationNodeKind.Clip
		};
		var second = new AnimationNode
		{
			Id = Guid.NewGuid(),
			Kind = AnimationNodeKind.Clip
		};
		var output = new AnimationNode
		{
			Id = Guid.NewGuid(),
			Kind = AnimationNodeKind.Output,
			Inputs = [first.Id]
		};

		return new AnimationGraph
		{
			Nodes = [first, second, output],
			Output = output.Id
		};
	}

	private static Dictionary<Guid, Vector2> Positions(AnimationGraph graph)
	{
		return new Dictionary<Guid, Vector2>
		{
			[graph.Nodes[0].Id] = Vector2.Zero,
			[graph.Nodes[1].Id] = new Vector2(0.0f, 150.0f),
			[graph.Nodes[2].Id] = new Vector2(400.0f, 0.0f)
		};
	}
}
