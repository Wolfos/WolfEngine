using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;

namespace WolfEngine.UI;

/// <summary>Razor work and await continuations are pumped exclusively by the gameplay thread.</summary>
internal sealed class UiDispatcher : Dispatcher
{
	private sealed class Context(UiDispatcher owner) : SynchronizationContext
	{
		public override void Post(SendOrPostCallback callback, object? state) => owner._pending.Enqueue((callback, state));
		public override SynchronizationContext CreateCopy() => this;
	}
	private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
	private readonly Context _context;
	private int _thread;
	public UiDispatcher() => _context = new(this);
	public void Bind()
	{
		Interlocked.CompareExchange(ref _thread, Environment.CurrentManagedThreadId, 0);
		if (!CheckAccess()) throw new InvalidOperationException("Gameplay UI must be updated on its owning gameplay thread.");
	}
	public override bool CheckAccess() => _thread == Environment.CurrentManagedThreadId;
	public void Pump()
	{
		Bind();
		var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(_context);
		// Bound each pump so a busy async component cannot monopolize the gameplay loop.
		try { for (var remaining = 256; remaining > 0 && _pending.TryDequeue(out var work); remaining--) work.Callback(work.State); }
		finally { SynchronizationContext.SetSynchronizationContext(previous); }
	}
	private Task<T> Run<T>(Func<Task<T>> work)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		async void Execute(object? _)
		{
			try { completion.SetResult(await work()); }
			catch (Exception exception) { completion.SetException(exception); }
		}
		if (CheckAccess())
		{
			var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(_context);
			try { Execute(null); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
		}
		else _pending.Enqueue((Execute, null));
		return completion.Task;
	}
	public override Task InvokeAsync(Action workItem) => Run(() => { workItem(); return Task.FromResult(true); });
	public override Task InvokeAsync(Func<Task> workItem) => Run(async () => { await workItem(); return true; });
	public override Task<T> InvokeAsync<T>(Func<T> workItem) => Run(() => Task.FromResult(workItem()));
	public override Task<T> InvokeAsync<T>(Func<Task<T>> workItem) => Run(workItem);
}
