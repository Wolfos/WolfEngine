using NUnit.Framework;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public sealed class WorldPauseTests
{
	private static readonly PauseReason Inventory = new PauseReason(0);
	private static readonly PauseReason Dialogue = new PauseReason(1);

	[Test]
	public void PausingStopsGameSystemsAndRunsPausedSystems()
	{
		var manager = new WorldManager();
		var world = manager.CreateWorld(WorldTag.Game);
		var game = new CountingSystem(WorldTag.Game);
		var paused = new CountingSystem(WorldTag.GamePaused);
		var anyGame = new CountingSystem(WorldTag.AnyGame);
		var all = new CountingSystem(WorldTag.All);
		manager.AddSystem(game);
		manager.AddSystem(paused);
		manager.AddSystem(anyGame);
		manager.AddSystem(all);

		RunFrame(manager);

		world.Pause(Inventory);
		RunFrame(manager);

		Assert.That(game.Calls, Is.EqualTo(3));
		Assert.That(paused.Calls, Is.EqualTo(3));
		Assert.That(anyGame.Calls, Is.EqualTo(6));
		Assert.That(all.Calls, Is.EqualTo(6));
	}

	[Test]
	public void ResumingRestoresGameSystems()
	{
		var manager = new WorldManager();
		var world = manager.CreateWorld(WorldTag.Game);
		var game = new CountingSystem(WorldTag.Game);
		var paused = new CountingSystem(WorldTag.GamePaused);
		manager.AddSystem(game);
		manager.AddSystem(paused);

		world.Pause(Inventory);
		RunFrame(manager);

		world.Resume(Inventory);
		RunFrame(manager);

		Assert.That(game.Calls, Is.EqualTo(3));
		Assert.That(paused.Calls, Is.EqualTo(3));
	}

	[Test]
	public void WorldStaysPausedUntilEveryReasonIsReleased()
	{
		var world = new World(WorldTag.Game);

		world.Pause(Inventory);
		world.Pause(Dialogue);
		world.Resume(Inventory);

		Assert.That(world.IsPaused, Is.True);
		Assert.That(world.PauseReasons, Is.EqualTo(Dialogue.Mask));

		world.Resume(Dialogue);

		Assert.That(world.IsPaused, Is.False);
		Assert.That(world.PauseReasons, Is.EqualTo(0u));
	}

	[Test]
	public void PausingForAHeldReasonAgainNeedsOnlyOneResume()
	{
		var world = new World(WorldTag.Game);

		world.Pause(Inventory);
		world.Pause(Inventory);
		world.Resume(Inventory);

		Assert.That(world.IsPaused, Is.False);
	}

	[Test]
	public void ResumingAReasonThatIsNotHeldLeavesOthersHeld()
	{
		var world = new World(WorldTag.Game);
		world.Pause(Dialogue);

		world.Resume(Inventory);

		Assert.That(world.PauseReasons, Is.EqualTo(Dialogue.Mask));
	}

	[Test]
	public void ClearPauseReasonsReleasesEveryReason()
	{
		var world = new World(WorldTag.Game);
		world.Pause(Inventory);
		world.Pause(Dialogue);

		world.ClearPauseReasons();

		Assert.That(world.IsPaused, Is.False);
		Assert.That(world.ExecutionTag, Is.EqualTo(WorldTag.Game));
	}

	[Test]
	public void APausedWorldKeepsItsGameTag()
	{
		var world = new World(WorldTag.Game);

		world.Pause(Inventory);

		Assert.That(world.Tag, Is.EqualTo(WorldTag.Game));
		Assert.That(world.ExecutionTag, Is.EqualTo(WorldTag.GamePaused));
	}

	[Test]
	public void APausedWorldStillMatchesTheGameHostMask()
	{
		var manager = new WorldManager();
		var world = manager.CreateWorld(WorldTag.Game);
		var anyGame = new CountingSystem(WorldTag.AnyGame);
		manager.AddSystem(anyGame);
		world.Pause(Inventory);

		manager.Update(0.016f, WorldTag.Game);

		Assert.That(anyGame.Calls, Is.EqualTo(1));
	}

	[Test]
	public void PauseDoesNotAffectWorldsThatAreNotGameWorlds()
	{
		var manager = new WorldManager();
		var world = manager.CreateWorld(WorldTag.Authoring);
		var authoring = new CountingSystem(WorldTag.Authoring);
		var paused = new CountingSystem(WorldTag.GamePaused);
		manager.AddSystem(authoring);
		manager.AddSystem(paused);
		world.Pause(Inventory);

		RunFrame(manager);

		Assert.That(world.ExecutionTag, Is.EqualTo(WorldTag.Authoring));
		Assert.That(authoring.Calls, Is.EqualTo(3));
		Assert.That(paused.Calls, Is.EqualTo(0));
	}

	[Test]
	public void AWorldTaggedAllPausesOnlyItsGamePart()
	{
		var world = new World(WorldTag.All);

		Assert.That(world.ExecutionTag, Is.EqualTo(WorldTag.Editor | WorldTag.Authoring | WorldTag.Game));

		world.Pause(Inventory);

		Assert.That(world.ExecutionTag, Is.EqualTo(WorldTag.Editor | WorldTag.Authoring | WorldTag.GamePaused));
	}

	[Test]
	public void PausingDuringAnUpdateStopsTheGameSystemsAfterIt()
	{
		var manager = new WorldManager();
		var world = manager.CreateWorld(WorldTag.Game);
		var pauser = new PausingSystem();
		var after = new CountingSystem(WorldTag.Game);
		manager.AddSystem(pauser);
		manager.AddSystem(after);

		manager.Update(0.016f, WorldTag.Game);

		Assert.That(world.IsPaused, Is.True);
		Assert.That(after.Calls, Is.EqualTo(0));
	}

	[Test]
	public void AWorldCannotBeCreatedAsPausedOnly()
	{
		Assert.Throws<ArgumentException>(() => new World(WorldTag.GamePaused));
	}

	[Test]
	public void EachReasonIsExactlyOneBit()
	{
		Assert.That(new PauseReason(0).Mask, Is.EqualTo(1u));
		Assert.That(new PauseReason(1).Mask, Is.EqualTo(2u));
		Assert.That(new PauseReason(2).Mask, Is.EqualTo(4u));
		Assert.That(new PauseReason(31).Mask, Is.EqualTo(0x8000_0000u));
		Assert.That(new PauseReason(31).Index, Is.EqualTo(31));
	}

	[Test]
	public void ReasonsOutsideTheThirtyTwoBitsAreRejected()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new PauseReason(-1));
		Assert.Throws<ArgumentOutOfRangeException>(() => new PauseReason(32));
	}

	[Test]
	public void TheDefaultReasonIsRejected()
	{
		var world = new World(WorldTag.Game);

		Assert.Throws<ArgumentException>(() => world.Pause(default));
		Assert.Throws<ArgumentException>(() => world.Resume(default));
	}

	/// <summary>Runs one update, physics step and pre-render: three calls for every system that runs.</summary>
	private static void RunFrame(WorldManager manager)
	{
		manager.PhysicsUpdate(0.016f, WorldTag.All);
		manager.Update(0.016f, WorldTag.All);
		manager.OnPreRender(0.016f, WorldTag.All);
	}

	private sealed class CountingSystem : IUpdate, IPhysicsUpdate, IPreRender
	{
		private readonly WorldTag _tag;

		public CountingSystem(WorldTag tag)
		{
			_tag = tag;
		}

		public int Calls { get; private set; }

		public WorldTag GetTag() => _tag;

		public void Update(float deltaTime, World world)
		{
			Calls++;
		}

		public void PhysicsUpdate(float fixedDeltaTime, World world)
		{
			Calls++;
		}

		public void PreRender(float deltaTime, World world)
		{
			Calls++;
		}
	}

	private sealed class PausingSystem : IUpdate
	{
		public WorldTag GetTag() => WorldTag.Game;

		public void Update(float deltaTime, World world)
		{
			world.Pause(Inventory);
		}
	}
}
