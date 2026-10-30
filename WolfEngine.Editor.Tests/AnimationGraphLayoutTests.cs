using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

public sealed class AnimationGraphLayoutTests
{
	[TestCase(480.0f, 320.0f)]
	[TestCase(1200.0f, 800.0f)]
	public void FitIncludesEveryNodeEvenWhenTheSavedGraphIsVeryWide(float width, float height)
	{
		var graph = Graph();
		for (var index = 0; index < graph.Nodes.Count; index++)
		{
			graph.Layout.Add(new AnimationNodeLayout { NodeId = graph.Nodes[index].Id, X = index * 500.0f - 2000.0f, Y = index * 150.0f });
		}

		var positions = AnimationGraphLayout.Positions(graph);
		var camera = AnimationGraphCamera.Fit(AnimationGraphLayout.Bounds(graph, positions), new Vector2(width, height));
		foreach (var node in graph.Nodes)
		{
			var min = camera.ToScreen(positions[node.Id]);
			var max = camera.ToScreen(positions[node.Id] + AnimationGraphLayout.NodeSize(node));
			Assert.Multiple(() =>
			{
				Assert.That(min.X, Is.GreaterThanOrEqualTo(39.9f));
				Assert.That(min.Y, Is.GreaterThanOrEqualTo(39.9f));
				Assert.That(max.X, Is.LessThanOrEqualTo(width - 39.9f));
				Assert.That(max.Y, Is.LessThanOrEqualTo(height - 39.9f));
			});
		}
	}

	[Test]
	public void ZoomPreservesTheGraphPointUnderTheCursor()
	{
		var camera = new AnimationGraphCamera(new Vector2(-200.0f, 350.0f), 0.6f);
		var anchor = new Vector2(180.0f, 240.0f);
		var graphPoint = camera.ToGraph(anchor);
		var zoomed = camera.ZoomAt(anchor, 1.4f);
		Assert.That(Vector2.Distance(zoomed.ToScreen(graphPoint), anchor), Is.LessThan(0.001f));
		Assert.That(zoomed.Zoom, Is.GreaterThan(camera.Zoom));
	}

	[Test]
	public void ArrangementFlowsTowardTheOutputWithoutOverlappingNodesOrChangingConnections()
	{
		var graph = Graph();
		var original = graph.Nodes.Select(node => node.Inputs.ToArray()).ToArray();
		var positions = AnimationGraphLayout.Arrange(graph);
		foreach (var target in graph.Nodes)
		{
			foreach (var source in target.Inputs)
			{
				Assert.That(positions[source].X + AnimationGraphLayout.NodeWidth, Is.LessThan(positions[target.Id].X));
			}
		}

		foreach (var first in graph.Nodes)
		{
			foreach (var second in graph.Nodes.Where(node => node.Id != first.Id))
			{
				var a = new GraphRect(positions[first.Id], AnimationGraphLayout.NodeSize(first));
				var b = new GraphRect(positions[second.Id], AnimationGraphLayout.NodeSize(second));
				var overlap = a.Min.X < b.Max.X && a.Max.X > b.Min.X && a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y;
				Assert.That(overlap, Is.False);
			}
		}

		Assert.That(graph.Nodes.Select(node => node.Inputs.ToArray()), Is.EqualTo(original));
		Assert.That(graph.Layout, Is.Empty);
	}

	[Test]
	public void MissingLayoutIsReadableWithoutEditingTheAssetAndCyclesRemainInspectable()
	{
		var graph = Graph();
		graph.Nodes[0].Inputs.Add(graph.Nodes[^1].Id);
		var positions = AnimationGraphLayout.Positions(graph);
		Assert.That(positions, Has.Count.EqualTo(graph.Nodes.Count));
		Assert.That(
			positions.Values.All(position => float.IsFinite(position.X) && float.IsFinite(position.Y)),
			Is.True);
		Assert.That(graph.Layout, Is.Empty);
	}

	[Test]
	public void ConnectionsRejectSelfReferencesCyclesAndOutputSources()
	{
		var graph = Graph();
		var clip = graph.Nodes[0];
		var blend = graph.Nodes[2];
		var output = graph.Nodes[^1];
		Assert.Multiple(() =>
		{
			Assert.That(AnimationGraphLayout.CanConnect(graph, clip.Id, blend.Id), Is.True);
			Assert.That(AnimationGraphLayout.CanConnect(graph, blend.Id, clip.Id), Is.False);
			Assert.That(AnimationGraphLayout.CanConnect(graph, clip.Id, clip.Id), Is.False);
			Assert.That(AnimationGraphLayout.CanConnect(graph, output.Id, blend.Id), Is.False);
			Assert.That(AnimationGraphLayout.CanConnect(graph, Guid.Empty, blend.Id), Is.True);
		});

		foreach (var target in graph.Nodes)
		{
			var candidates = AnimationGraphLayout.ConnectionSources(graph, target.Id);
			foreach (var source in graph.Nodes)
			{
				Assert.That(candidates.Contains(source.Id), Is.EqualTo(AnimationGraphLayout.CanConnect(graph, source.Id, target.Id)));
			}
		}
	}

	[Test]
	public void PortsRemainInsideNodesWithManyInputs()
	{
		var node = new AnimationNode
		{
			Kind = AnimationNodeKind.StateMachine
		};
		node.Inputs.AddRange(Enumerable.Repeat(Guid.Empty, 40));
		var position = new Vector2(150.0f, -200.0f);
		var size = AnimationGraphLayout.NodeSize(node);
		for (var index = 0; index < node.Inputs.Count; index++)
		{
			var port = AnimationGraphLayout.InputPosition(node, position, index);
			Assert.That(port.Y, Is.InRange(position.Y + AnimationGraphLayout.HeaderHeight, position.Y + size.Y));
		}
	}

	private static AnimationGraph Graph()
	{
		var idle = new AnimationNode
		{
			Name = "Idle",
			Kind = AnimationNodeKind.Clip
		};
		var walk = new AnimationNode
		{
			Name = "Walk",
			Kind = AnimationNodeKind.Clip
		};
		var blend = new AnimationNode
		{
			Name = "Locomotion",
			Kind = AnimationNodeKind.Blend,
			Inputs = [idle.Id, walk.Id]
		};
		var jump = new AnimationNode
		{
			Name = "Jump",
			Kind = AnimationNodeKind.Clip
		};
		var state = new AnimationNode
		{
			Name = "Movement",
			Kind = AnimationNodeKind.StateMachine,
			Inputs = [blend.Id, jump.Id]
		};
		var output = new AnimationNode
		{
			Name = "Output",
			Kind = AnimationNodeKind.Output,
			Inputs = [state.Id]
		};
		return new AnimationGraph
		{
			Nodes = [idle, walk, blend, jump, state, output],
			Output = output.Id
		};
	}
}
