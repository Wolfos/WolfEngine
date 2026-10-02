# Gameplay fonts

`.ttf` and `.otf` sources in a project's `Assets` directory are imported automatically.
The editor also exposes **Import > Import Font…**. Select the resulting Font asset
to inspect the baked RGB distance field and a shaped-text proof at 18, 32 and 64px.

The compiler belongs to `WolfEngine.Editor.Tooling`: HarfBuzzSharp loads the
default OpenType face, supplies metrics and shapes the proof text. A small
`hb_draw` bridge obtains quadratic/cubic outlines from its bundled native library.
Remora.MSDFGen supplies edge coloring, curve distances and MSDF generation.
Import uses overlap-safe nonzero-winding boundaries (1/16-texel flattening tolerance) and scalar-distance-guided
interpolation correction to handle junction and channel artifacts in the legacy port.
Remora and outline baking are tooling-only. The gameplay UI runtime uses HarfBuzz
for shaping the font blob stored in the cooked artifact.

Defaults: 48 pixels/em, a 6-pixel full distance range, and a 512-pixel atlas width.
Settings are serialized in the source `.meta` file. Reimport preserves the asset ID.
The atlas is linear RGBA8 MTSDF: RGB contains multi-channel distance, alpha true
signed distance, and the contour is 0.5. Atlas rows are top-down; glyph plane
bounds, advances and font metrics are in Y-up em units. The `WFNT` versioned
artifact contains metrics, original glyph IDs, codepoint mapping, atlas pixels,
and the original font blob for later runtime shaping. Diagnostic atlas PNGs are
opaque RGB (not the runtime alpha channel); preview filenames are content-addressed.

This milestone covers Basic Latin, `.notdef`, proof-text glyphs and the GSUB
substitution closure reachable from that coverage (including contextual variants
and ligatures). It is not a complete international-font bake.
Variable-font axis selection, additional faces, per-glyph fallback, configurable
coverage and atlas paging remain separate work.

## Gameplay CSS

The project contains only the source `.ttf`/`.otf` and its `.meta`. Cooked atlases,
metrics and shaping data live in `Library`; no authored `.wolffont` is needed.
The font inspector's **Copy CSS @font-face** button supplies the source reference:

```css
@font-face {
    font-family: "Inter";
    src: url("/Assets/Fonts/Inter.ttf");
}
.hud {
    font-family: "Inter";
    font-size: 18px;
    line-height: 1.2;
}
.counter { white-space: nowrap; }
```

URLs are project-root `Assets/` paths, not filesystem paths or network URLs.
The first declared face is the sheet's default. Family lists select the first
available declared face; font-weight/style variants and per-character fallback
are not implemented. Line-height supports `normal`, unitless multiples and pixels.
Text wraps at whitespace, with glyph-level wrapping for overlong words. Full
Unicode line-breaking and mixed-direction paragraph layout are future work.

The same cached HarfBuzz positions measure Yoga text and generate one quad per
visible glyph. Screen-space and texture surfaces use the same MSDF shader;
backgrounds can share atlas batches and indices remain 32-bit. Font atlases are
shared across surfaces. Bounded shaping/measurement caches limit counter churn.
Missing or unbaked characters use `.notdef` and emit a diagnostic.

Build tooling discovers `@font-face` references in embedded gameplay CSS, includes
their cooked artifacts automatically, and writes stable-ID source mappings into
the bootstrap manifest. Runtime resolution reads the pack, never source fonts or
the editor's `Library`. Missing referenced font assets fail the build.
