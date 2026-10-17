namespace WolfEngine.Rendering;

/// <summary>
/// One view's draw database, as a source of the draws in the shared GPU draw tables. Every view's database
/// allocates from one handle registry into one set of tables, so the tables are updated once per frame from all
/// of them, and each draw is tagged with the view that owns it.
/// </summary>
public readonly record struct GpuDrawSource(RenderViewId Owner, GpuDrawDatabase Database);
