# Gameplay CSS essentials

Razor components and CSS remain in the gameplay project. This is an intentionally
small flex-layout subset, not a browser: elements default to flex containers and
relative positioning, and dimensions use Yoga's border-box sizing.

## Screen-space mouse input

Use Blazor event attributes in gameplay Razor components. Callbacks passed into
child components should be `EventCallback` or `EventCallback<MouseEventArgs>`.

Add these imports to the gameplay project's `_Imports.razor` (or the component):

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
```

The web import enables Razor's event directives. Without it, Razor can compile
`@onclick` as an inert literal HTML attribute instead of an event binding.

```razor
<button class="action" @onclick="Increment" @onclick:stopPropagation="true">
    <span>Clicks: @_clicks</span>
</button>
@code {
    private int _clicks;
    private void Increment() => _clicks++;
}
```

Supported attributes: `@onclick`, `@onmousedown`, `@onmouseup`, `@onmousemove`,
`@onmouseenter`, and `@onmouseleave`. Click/down/up/move bubble, with
`:stopPropagation` modifiers; enter/leave do not. Only primary-button matched
presses/releases generate clicks. Use `@key` for stable identity in lists.
A disabled button consumes input without invoking callbacks.

Buttons have no implicit appearance. Style them with `:hover`, `:active`, and
`:disabled`. `pointer-events: auto | none` inherits; children can restore `auto`.
Hit-testing uses element boxes, reverse paint order, and nested rectangular clips.
Paint-only changes reuse layout; stationary frames do not rebuild. Noninteractive
HUD regions pass input to gameplay. Only the displayed screen surface handles
input; texture surfaces never do.

Mouse input is queued and drained before gameplay/physics. Each button retains
its original owner until release, even when dragged outside UI. Focus loss,
hidden viewports, target removal, disposal/hot reload, or leaving running Play
cancel captures without clicking. Editor ImGui receives input independently.

Creation, parameter updates, events, and async Razor continuations belong to the
gameplay thread. Async callbacks can await without blocking updates; their renders
publish automatically. Viewport resize requests are deferred to the UI pump.
Custom hosts must call `IInputSystem.ProcessPointerInput` with the registered
`IPointerInputRouter` every update, even when interaction is disabled, and supply
viewport origin/size in window logical points. The editor and standalone runtime
already do this, including DPI conversion.

Keyboard activation, focus navigation, text entry, scrolling, double-click,
context menus, and world-space picking remain deferred.

```css
:root { font-size: 20px; }
.hud {
    position: absolute;
    right: 2rem;
    bottom: 24px;
    width: 300px;
    padding: 12px 20px 16px;
    padding-left: 1em;
    font-size: 90%;
    overflow: hidden;
}
.centered { width: 200px; margin: 0 auto; }
.fill { position: absolute; inset: 10px 20px; }
```

## Spacing and units

`padding` and `margin` support the standard 1–4-value order: all sides;
vertical/horizontal; top/horizontal/bottom; top/right/bottom/left. Each has
`-top`, `-right`, `-bottom` and `-left` longhands. Declaration order and selector
specificity apply normally; a later shorthand replaces all four sides.

Supported lengths are `px`, `%`, `em`, `rem`, `vw`, `vh`, and unitless pixels for
compatibility with the initial engine subset. Padding is nonnegative; margins
can be negative or `auto`. Percentage padding and margins, including vertical
sides, are resolved against the containing width by Yoga. Auto margins consume
available flex space; they do not create browser block-layout margin collapsing.

`font-size` inherits by default and supports these units plus `inherit`. Its `em`
and `%` values reference the parent's font size; its `rem` references the UI root.
Set that root with `:root`; its initial font size is 16 logical pixels. In spacing,
dimensions and offsets, `em` references the element's final computed font size,
regardless of declaration order. Viewport font sizes recompute when resized.

## Anchoring and clipping

`text-align: left | center | right` is inherited and aligns each explicit or wrapped
line within the text node's content width. `inherit` is supported; alignment does
not change text measurement or trigger Yoga layout. Flex alignment still positions
the text box independently. Whitespace/newline handling is unchanged for now.

`left`, `top`, `right`, `bottom` and the 1–4-value `inset` shorthand accept lengths
or `auto`. Use `position: absolute` for corner anchors. Opposing insets stretch
auto-sized elements. Yoga's relative containers provide the containing blocks.

`overflow: visible` is the default. `overflow: hidden` clips descendants to the
element's rectangular padding box (there are currently no borders). Nested clips
intersect and restore correctly for siblings. Both screen and render-to-texture
surfaces use the same physical-pixel scissors, including HiDPI scaling. Fully
clipped quads are skipped, and adjacent geometry with identical clip/material
state stays batched. Rounded-corner clipping, separate overflow axes and scrolling
are not implemented yet.

## Diagnostics

Unsupported properties and unsupported/invalid values emit a warning once per
distinct declaration per stylesheet. They are ignored rather than overriding
earlier valid declarations. This includes inline styles and unimplemented
`@font-face` descriptors. The parser is still a subset; functions such as `calc()`
and `var()`, `!important`, general at-rules and browser typography/layout keywords
are not supported.

Format numbers embedded in CSS with invariant culture, e.g.
`value.ToString(CultureInfo.InvariantCulture)`: CSS requires a decimal point, even
when the gameplay/editor locale uses commas.
