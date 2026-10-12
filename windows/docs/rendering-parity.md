# Rendering parity — Phase B

This record covers the source-verified rendering work for the Windows port's
Phase B. It distinguishes an upstream or mathematical oracle (**A**), a
macOS/Photoshop binary-pixel fixture (**B**), and an independently rendered
Windows regression fixture or round trip (**C**). A passed A/C fixture is not
presented as an unmeasured B comparison.

Last verified: 2026-10-12

## Scope and source evidence

| Area | Upstream evidence | Windows decision |
|---|---|---|
| Soft Light | `5a8f6cee7cf25979661630dbbfc6590e95c99d55` changes macOS from the Core Graphics blend path to `CISoftLightBlendMode`; its test uses a 0.5 backdrop and 0.9 source, expecting about 170/255 within 2. | Keep Skia's `SKBlendMode.SoftLight`; its pixels meet the upstream golden and the independent W3C/Photoshop source-over equation. |
| Masks, adjustments, clipping stacks | `93de9e7`, `54dd707`, `6dc2219`, and `8ab32c9` map masks through document space, use base coverage for clipping stacks, and make adjustment/filter composition operate on the correctly covered surface. | Map every enabled mask through its placement and target grid; preserve base alpha after stack composition; apply folder coverage once to a completed stack; use source-layer alpha for detached live masks; apply the same mapped coverage to adjustment layers. |
| Add Noise origin | `8c0417b52312da5ad040b7235888f2a457473a26` anchors noise in image coordinates so separately rendered regions line up. | Keep the existing origin-aware adjustment path and lock it down with whole-document versus region/tile byte comparisons. |

The implementation remains a CPU/Skia renderer. Metal/GPU residency and
CoreImage implementation details are intentionally not ported.

## Golden and regression results

All values below are RGBA bytes. “Exact” means every asserted channel matched;
it is not an assertion that a separate macOS binary fixture was available.

| Area | Oracle / fixture | Expected | Windows actual | Max error | Mean error | Tolerance | Evidence |
|---|---|---|---|---:|---:|---:|---|
| Soft Light | Upstream Photoshop case: backdrop 128, source 230 | `[170,170,170,255]` | `[170,170,170,255]` | 0 | 0 | 2 | A + C |
| Soft Light | Independent colored W3C/Photoshop formula | `[26,170,196,255]` | `[26,170,196,255]` | 0 | 0 | 2 | A + C |
| Soft Light | Independent semi-transparent source-over formula | `[126,115,197,166]` | `[126,115,197,166]` | 0 | 0 | 2 | A + C |
| Soft Light | Transparent source leaves backdrop | `[80,150,220,192]` | `[80,150,220,192]` | 0 | 0 | 1 | A + C |
| Blend controls | Normal, Multiply, Screen, Overlay analytical controls | Exact | Exact | 0 | 0 | 1 | A + C |
| Clipping stack | Upstream Linear Dodge covered/base-only fixture | `[153,64,26,255]`, `[179,128,102,255]` | `[153,64,26,255]`, `[179,128,103,255]` | 1 | 0.125 | 1 | A + C |
| Clipping stack | Hidden unrelated sibling does not interrupt a visible Linear Dodge stack | Same as uninterrupted stack | Same as uninterrupted stack | 0 | 0 | 1 | A + C |
| Clipping alpha | 50% base with opaque clipped child | `[255,0,0,128]` | `[255,0,0,128]` | 0 | 0 | 1 | A + C |
| Folder-mask stack | 50% folder mask over white base plus red clipped child | `[255,0,0,128]` | `[255,0,0,128]` | 0 | 0 | 1 | A + C |
| Masks | Linked rotation, unlinked placement/rotation, 1×1 placed-gray footprint, edge background, nested masks | Exact specified pixels | Exact | 0 | 0 | 0 | A + C |
| Live masks | Hidden non-contiguous source and source blend isolation | Exact specified pixels | Exact | 0 | 0 | 0 | A + C |
| Adjustments | Detached clipped adjustment has no visual stack to run in | `[50,80,100,255]` | `[50,80,100,255]` | 0 | 0 | 0 | A + C |
| Add Noise | Off-origin region equals the same pixels from a whole render | Byte-for-byte equal | Byte-for-byte equal | 0 | 0 | 0 | C, source-derived anchor |
| Add Noise | Every tile over scaled/translated content equals a whole render | Byte-for-byte equal | Byte-for-byte equal | 0 | 0 | 0 | C, source-derived anchor |
| Format safety | Placed mask plus live link, and black-edge placed-mask, `.comp` round trips | Byte-for-byte equal | Byte-for-byte equal | 0 | 0 | 0 | C |

The fixtures live in
`windows/tests/Compositor.Core.Tests/RenderingParityTests.cs`.

## Implementation notes

### Masks and adjustment coverage

An enabled layer or folder mask is sampled by composing its placement into
document coordinates and then into the target bitmap's grid. This replaces the
former axis-aligned-bounds approximation. It preserves linked rotation/flip and
lets a deliberately unlinked mask remain in its own placement. Pixels outside a
placed mask use the existing `LayerMask.Background` edge-majority behavior.

Own masks are applied before effects; enclosing folder masks are applied after
the layer/effect raster is placed. Adjustment layers use the same document-space
coverage rather than a rectangle derived only from their own mask bounds.

Newly created Gray8 masks now retain their Gray8 thumbnail rather than an
alpha-only thumbnail. This matters because the established edge-majority
fallback reads the thumbnail; it now agrees with the project-load path and
cannot change the result of a placed mask merely by saving and reopening it.

### Clipping and live masks

A contiguous visible clipping stack captures the base alpha, blends children
over an opaque copy of the base, then restores that alpha exactly once. This
prevents an opaque child from changing a half-opaque base from alpha 0.5 to
0.75. The completed stack is then composited using the base layer's blend mode
and opacity. Enclosing folder masks are applied once around this completed
stack, not once per member. Stack discovery ignores hidden siblings just as the
upstream visibility-filtered layer list does.

A non-contiguous or hidden source remains a live alpha source but is not a
visible stack base. Its own masks, effective opacity, and source chain form the
coverage; its visibility, colour, and blend mode do not leak into the dependent
layer. A clipped adjustment only runs as a contiguous member of a visible stack,
matching the upstream visual-stack rule.

### Pixel-effect input

Pixel effects continue to receive premultiplied input. Applying a mask creates a
premultiplied copy; an unmasked asset is copied into that form only for an
effect. This retains the existing effect contract and avoids treating straight
asset RGB as premultiplied alpha.

## Compatibility and non-goals

- `.comp` remains format version 11. No manifest field, JSON key, serialized
  enum, layer ID, mask filename, or parent representation changed.
- No localization resource changed because this is Core-only rendering work.
- Camera Raw, PSD import routing, renderer architecture, and interactive UI
  behavior are outside this batch.
- Existing PNG/PSD/import and document round-trip coverage remains in place;
  the new format test specifically covers the changed placed-mask/live-link
  rendering path.

## Remaining parity evidence gaps

1. There is no usable macOS/Photoshop binary fixture for a broad Soft Light,
   clipping, mask, or scaled-adjustment matrix. The upstream gray Soft Light
   expected pixel is covered, but broader B-level comparison is still required
   before claiming exact cross-platform pixels.
2. The destructive `FilterEdits.ApplyAdjustment` path should receive a
   separately captured macOS reference for a scaled/translated Add Noise
   adjustment. The document renderer's region/tile invariance is now covered.
3. `LayerMask.PlacementMoving` does not yet mirror macOS's
   `following(from:to:)` semantics for every later transform of a separated,
   linked mask. That is an interaction/model follow-up, not a rendering-format
   change.
4. GPU texture residency, command-buffer scheduling, and performance behavior
   are Apple implementation details rather than functional parity evidence for
   the current Skia backend.
