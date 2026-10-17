using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Remora.MSDFGen;

namespace WolfEngine.Editor.Tooling.Fonts;

/// <summary>HarfBuzzSharp does not expose hb-draw. This small bridge uses its bundled HarfBuzz binary.</summary>
internal sealed unsafe class HarfBuzzOutlineReader : IDisposable
{
	private readonly nint _functions;
	private readonly float _unitsPerEm;
	private readonly Shape _shape = new();
	private Contour? _contour;
	private Vector2 _current;
	private Vector2 _start;
	private Exception? _callbackException;

	public HarfBuzzOutlineReader(int unitsPerEm)
	{
		_unitsPerEm = unitsPerEm;
		_functions = hb_draw_funcs_create();
		if (_functions == 0) throw new InvalidOperationException("Unable to create HarfBuzz outline callbacks.");
		hb_draw_funcs_set_move_to_func(_functions, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, float, float, nint, void>)&Move, 0, 0);
		hb_draw_funcs_set_line_to_func(_functions, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, float, float, nint, void>)&Line, 0, 0);
		hb_draw_funcs_set_quadratic_to_func(_functions, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, float, float, float, float, nint, void>)&Quadratic, 0, 0);
		hb_draw_funcs_set_cubic_to_func(_functions, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, float, float, float, float, float, float, nint, void>)&Cubic, 0, 0);
		hb_draw_funcs_set_close_path_func(_functions, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&Close, 0, 0);
	}

	public Shape Read(Font font, uint glyph)
	{
		var handle = GCHandle.Alloc(this);
		try
		{
			hb_font_draw_glyph(font.Handle, glyph, _functions, GCHandle.ToIntPtr(handle));
			GC.KeepAlive(font);
			if (_callbackException is not null) throw new InvalidDataException("Font outline extraction failed.", _callbackException);
			if (!_shape.Validate()) throw new InvalidDataException($"Invalid outline for glyph {glyph}.");
			_shape.Normalize();
			return _shape;
		}
		finally { handle.Free(); }
	}

	private static HarfBuzzOutlineReader Get(nint data) => (HarfBuzzOutlineReader)GCHandle.FromIntPtr(data).Target!;
	private Vector2 Point(float x, float y) => new(x / _unitsPerEm, y / _unitsPerEm);
	// Never propagate managed exceptions across the native callback boundary.
	private static void Run(nint data, Action<HarfBuzzOutlineReader> callback)
	{
		var reader = Get(data);
		if (reader._callbackException is not null) return;
		try { callback(reader); }
		catch (Exception exception) { reader._callbackException = exception; }
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void Move(nint funcs, nint data, nint state, float x, float y, nint user) => Run(data, r =>
	{
		r._contour = new Contour();
		r._shape.Contours.Add(r._contour);
		r._current = r._start = r.Point(x, y);
	});
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void Line(nint funcs, nint data, nint state, float x, float y, nint user) => Run(data, r =>
	{
		var end = r.Point(x, y);
		if (end != r._current) r._contour!.Edges.Add(new LinearSegment(r._current, end, EdgeColor.White));
		r._current = end;
	});
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void Quadratic(nint funcs, nint data, nint state, float cx, float cy, float x, float y, nint user) => Run(data, r =>
	{
		var end = r.Point(x, y);
		r._contour!.Edges.Add(new QuadraticSegment(r._current, r.Point(cx, cy), end, EdgeColor.White));
		r._current = end;
	});
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void Cubic(nint funcs, nint data, nint state, float cx, float cy, float dx, float dy, float x, float y, nint user) => Run(data, r =>
	{
		var end = r.Point(x, y);
		r._contour!.Edges.Add(new CubicSegment(r._current, r.Point(cx, cy), r.Point(dx, dy), end, EdgeColor.White));
		r._current = end;
	});
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void Close(nint funcs, nint data, nint state, nint user) => Run(data, r =>
	{
		if (r._current != r._start) r._contour!.Edges.Add(new LinearSegment(r._current, r._start, EdgeColor.White));
		r._current = r._start;
	});

	public void Dispose() => hb_draw_funcs_destroy(_functions);

	private const string Library = "libHarfBuzzSharp";
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint hb_draw_funcs_create();
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_draw_funcs_destroy(nint funcs);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_font_draw_glyph(nint font, uint glyph, nint funcs, nint data);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_draw_funcs_set_move_to_func(nint funcs, nint callback, nint user, nint destroy);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_draw_funcs_set_line_to_func(nint funcs, nint callback, nint user, nint destroy);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_draw_funcs_set_quadratic_to_func(nint funcs, nint callback, nint user, nint destroy);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_draw_funcs_set_cubic_to_func(nint funcs, nint callback, nint user, nint destroy);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_draw_funcs_set_close_path_func(nint funcs, nint callback, nint user, nint destroy);
}
