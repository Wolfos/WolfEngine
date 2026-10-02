using System.Runtime.InteropServices;
using HarfBuzzSharp;

namespace WolfEngine.Editor.Tooling.Fonts;

/// <summary>Includes substitutions reachable from the imported character coverage, not just a proof string.</summary>
internal static class HarfBuzzGlyphClosure
{
	public static void Expand(Face face, SortedSet<uint> glyphs)
	{
		var set = hb_set_create();
		var lookups = hb_set_create();
		try
		{
			if (set == 0 || lookups == 0) throw new InvalidOperationException("Unable to allocate HarfBuzz glyph closure.");
			foreach (var glyph in glyphs) hb_set_add(set, glyph);
			var count = hb_ot_layout_table_get_lookup_count(face.Handle, 0x47535542); // GSUB
			for (uint i = 0; i < count; i++) hb_set_add(lookups, i);
			hb_ot_layout_lookups_substitute_closure(face.Handle, lookups, set);
			uint next = uint.MaxValue;
			while (hb_set_next(set, ref next) != 0) glyphs.Add(next);
			GC.KeepAlive(face);
		}
		finally { hb_set_destroy(lookups); hb_set_destroy(set); }
	}
	private const string Library = "libHarfBuzzSharp";
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint hb_set_create();
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_set_destroy(nint set);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_set_add(nint set, uint value);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int hb_set_next(nint set, ref uint value);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint hb_ot_layout_table_get_lookup_count(nint face, uint tag);
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void hb_ot_layout_lookups_substitute_closure(nint face, nint lookups, nint glyphs);
}
