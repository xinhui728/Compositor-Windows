# Upstream parity roadmap: macOS 1.3.7 to 1.4.5

This is the source-based parity ledger for the Windows port. It is deliberately
about observable behavior and data correctness, not a checklist copied from
release notes. Do not merge macOS source into the Windows branch to close an
item: port the behavior into the C# / Avalonia / SkiaSharp architecture and add
tests around it.

Last audited: 2026-10-07

## Evidence and baseline

### Upstream tags

The local tags were checked against the upstream tag names and the source at
each tag was diffed. `v1.4` is the actual upstream spelling for the 1.4.0
release; there is no `v1.4.0` tag.

| Release | Tag commit |
|---|---|
| 1.3.7 | `9e2894ed5cc713e42d220de24b42d2703e6b341c` |
| 1.4.0 | `v1.4` — `d04f1590828a902ab8f4f3a28c4dbaf26edd43db` |
| 1.4.1 | `0d9986fcacf852123622abf08f84feb2b7172c45` |
| 1.4.2 | `ed9c2809a3cf618cc8b74bffb6f415729ca7237d` |
| 1.4.3 | `557d2f05dc2ec627a845b8cde0f76239cb16da1e` |
| 1.4.4 | `ec0255974d1f281c7a134bfd0ead33aacb73c12f` |
| 1.4.5 | `086f1631573ccb2b57644e53b52bf1488fc976aa` |

`git rev-list --count v1.3.7..v1.4.5` returns **56 commits**. Of those,
**39 modify production `Compositor/` source**; the remainder are release,
appcast, project-version, documentation, merge, or test-only commits. The full
source diff is 78 files, 5,108 insertions, and 591 deletions.

### What the current Windows baseline really is

There are three useful answers, and they must not be conflated:

| Evidence | Result |
|---|---|
| Product metadata | `windows/src/Compositor.Desktop/Compositor.Desktop.csproj` reports `1.3.7`; `windows/README.md` also describes the port as 1.3.7. |
| Git ancestry | `v1.3.7`, `v1.4`, `v1.4.1`, and `v1.4.2` are ancestors of `develop`; `v1.4.3`, `v1.4.4`, and `v1.4.5` are not. The direct upstream merge point after `v1.4.2` is `0787b60` (the 1.4.2 update-feed commit). |
| Windows implementation | The C# port has manually implemented much of the 1.4–1.4.2 behavior, but it is not source-identical to macOS and has deliberate platform differences. |

Accordingly, call the Windows product **version-labelled 1.3.7, with source
history through upstream 1.4.2 and selective/manual parity work**. It is not
safe to claim exact parity with any single macOS tag. The first upstream release
whose commits are wholly outside the branch ancestry is **v1.4.3**.

### Audit method

The matrix below was made from `git log`, `git show`, and `git diff` between the
tags, then checked against `windows/src/Compositor.Core` and
`windows/src/Compositor.Desktop`. A grouped matrix row lists every production
commit it represents; grouping only combines follow-up commits that refine the
same behavior.

Status meanings:

| Status | Meaning |
|---|---|
| ✅ Already implemented | Equivalent user-visible behavior exists in the Windows source. |
| 🟡 Partially implemented | A substantial part exists, but an interaction, integration, or correctness detail is absent. |
| ❌ Missing | No equivalent Windows behavior is present. |
| ⚠️ Different implementation | Windows has the feature, but source inspection shows different rendering or interaction semantics. |
| 🍎 Apple-only | The upstream change is an Apple/Metal implementation detail. Windows has a different functional path or no need to port that API directly. |

## Feature gap matrix

| Version | Upstream change | macOS implementation | Windows status | Windows files | Difficulty | Priority |
|---|---|---|---|---|---|---|
| 1.4 | `8c0417b` — sharp adjustment surfaces; PSD Levels, Hue/Saturation, and placed-mask import fixes; adjustment-noise anchoring | Renders adjustment offscreens at pixel resolution; PSD `hue2`, Levels gamma, and mask patch bounds/default are decoded correctly. | 🟡 Core PSD reader/builder carries mask bounds/default and adjustment parsing. Desktop now routes PSD/PSB to editable documents (or a wrapper group in an existing document), with self-authored PSD/PSB mask/clipping/blend fixtures. A localized conversion-confirmation report and macOS-derived scaled-noise oracle remain. | `Core/IO/PSD/PsdReader.cs`, `PsdDocumentBuilder.cs`, `PsdTypes.cs`, `Core/Document/LayerPlacement.cs`, `Core/Rendering/DocumentRenderer.cs`, `Desktop/MainWindow.cs`, PSD tests | M | P0 |
| 1.4 | `fe7a83d` — Photoshop positive Hue/Saturation | Positive saturation divides by the remaining saturation; negative saturation scales toward gray. | ✅ `AdjustedSaturation` has the same positive/negative rule. | `Core/Pixels/AdjustmentOperators.cs` | S | P1 |
| 1.4 | `af3b568` — Levels/Hue-Saturation multicore and cached LUTs | Splits pixel work across cores and keeps the last eight Hue/Saturation cubes. | 🟡 Correct operators exist, but C# `ApplyLevels` and `ApplyHueSaturation` iterate rows serially and do not keep the matching cube cache. | `Core/Pixels/AdjustmentOperators.cs`, `LevelsPixels.cs` | M | P3 |
| 1.4 | `cdf8674`, `4b04323`, `5708ed3` — initial GPU canvas, selected-pixel move, brush/gradient/smudge previews | Adds `GPUCanvas` and keeps textures resident during interactive edits. | 🍎 The Metal texture pipeline is not portable. Windows has functional Skia canvas previews and CPU document rendering, but not this GPU residency model. | `Core/Rendering/DocumentRenderer.cs`, `Desktop/CanvasView.cs`, `Desktop/MainWindow.cs` | XL | P3 |
| 1.4 | `dcf1c66`, `d0135c8` — cache visible layers and hierarchy | Avoids rebuilding visibility/hierarchy on every canvas frame. | 🟡 `LayerHierarchy` and document rendering exist, but no source-equivalent retained hierarchy/visible-layer cache was found. | `Core/Model/LayerHierarchy.cs`, `Core/Rendering/DocumentRenderer.cs` | M | P3 |
| 1.4 | `93de9e7`, `54dd707`, `6dc2219`, `8ab32c9` — GPU masks, adjustments, clipping stacks, all blend modes | Extends the GPU path to masks, adjustment layers, blur/noise/grain, and correct clipping-stack blending. | ⚠️ Windows has CPU/Skia support for masks, clipping stacks, effects, and adjustments, but needs cross-platform pixel fixtures for equivalent blend/noise results. | `Core/Rendering/DocumentRenderer.cs`, `Core/Pixels/AdjustmentOperators.cs`, `Core/Pixels/BlendModes.cs` | L | P1 |
| 1.4 | `91cb0b1`, `f773f7b`, `4c5e1a6`, `d04f159` — Metal warps, all-frame coverage, texture memory/copy fixes | Completes GPU drawing and reduces GPU memory/copy churn; `d04f159` is why tag `v1.4` points after the release commit. | 🍎 Metal/QuartzCore resource lifetime and command-buffer ordering have no direct Avalonia equivalent. Do not port the APIs; benchmark the Skia path before any renderer architecture work. | `Core/Rendering/DocumentRenderer.cs`, `Desktop/CanvasView.cs` | XL | P3 |
| 1.4 | `3ccc125` — keep prior filter preview while a larger result is rendering | Retains correctly placed previous preview instead of flickering to the original layer. | ✅ `FilterPreview` preserves the last successful generated asset when a subsequent preview refuses/fails. | `Core/Document/FilterPreview.cs`, `tests/Compositor.Core.Tests/FilterPreviewTests.cs` | S | P2 |
| 1.4 | `5a8f6ce` — Photoshop-like Soft Light | Uses `CISoftLightBlendMode` because Core Graphics differed visibly. | ⚠️ Windows maps directly to `SKBlendMode.SoftLight`; it needs Photoshop/macOS golden-pixel comparison before calling this equivalent. | `Core/Pixels/BlendModes.cs` | M | P1 |
| 1.4 | `60bde4f` — Shift constrains selected-pixel drag | Locks a moved selection’s pixels horizontally or vertically. | ✅ `CanvasView` constrains only the floating selected-pixel drag through `SelectionEdits.ConstrainPixelDrag`; it chooses the current dominant axis and returns to free movement when Shift is released. | `Desktop/CanvasView.cs`, `Core/Document/SelectionEdits.cs`, `tests/Compositor.Core.Tests/SelectionPixelsTests.cs` | S | P2 |
| 1.4 | `451281e` — Cmd-A from Layers selects canvas | Layer-list focus no longer consumes Select All as row selection. | ✅ Window shortcut dispatch routes Ctrl+A to `SelectionEdits.SelectAll` unless a text field owns the edit chord. | `Desktop/MainWindow.cs`, `Core/IO/Shortcuts.cs` | S | P2 |
| 1.4.1 | `133c34a` — precise paint-refusal feedback; Select All then Inverse clears selection | Explains why painting cannot proceed; canonicalizes full inverse to no active selection. | 🟡 `SelectionEdits.Invert` now canonicalizes a full inverse to `DocumentSelection.All`; Windows still has only the existing tool-specific paint-refusal messages rather than every upstream reason. | `Desktop/MainWindow.cs`, `Core/Document/SelectionEdits.cs`, `tests/Compositor.Core.Tests/SelectionCanonicalizationTests.cs` | S | P2 |
| 1.4.1 | `d3170ab` — Clone Stamp and Blur at native layer resolution | Samples and paints scaled/rotated/flipped layers on their own pixel grid. | ⚠️ Output is written on the native grid, but Clone/Blur sampling is explicitly made at document size; this is not the same scaled-layer behavior. | `Core/Document/BrushEdits.cs` | L | P1 |
| 1.4.1 | `67cc31e` — transform Apply/Cancel and scaled-size labels | Only persistent transforms show Apply/Cancel; layer rows expose non-100% scale. | 🟡 Direct canvas transforms are one undo step on release, but there is no equivalent transform inspector/scale label workflow. | `Desktop/CanvasView.cs`, `Desktop/MainWindow.cs` | M | P2 |
| 1.4.1 | `b3419ab` — selection-aware masks and mask-alone view | Add Mask reveals selection; Option-add hides selection; Option-click displays only the mask. | ✅ `LayerMaskEdits.Add` creates existing Gray8 mask assets from selections and consumes the selection; Alt-clicking the Avalonia mask target toggles a view-only grayscale mask preview without history or serialization changes. | `Desktop/MainWindow.cs`, `Desktop/CanvasView.cs`, `Core/Document/LayerMaskEdits.cs`, `Core/Rendering/MaskPreviewRenderer.cs`, tests | M | P1 |
| 1.4.1 | `bca8f13` — live Command Auto Select and Shift aspect-lock state | Held modifiers invert Auto Select/aspect lock and update the controls while held. | 🟡 Shift toggles transform aspect ratio during a drag, but Windows has no Auto Select option or live modifier-state UI. | `Desktop/CanvasView.cs`, `Desktop/ToolOptions.cs`, `Desktop/ToolOptionsBar.cs` | S | P2 |
| 1.4.1 | `1faf7a0` — reorderable tabs and overflow menu | Drag tabs to reorder; hide excess tabs in a menu while pinning the selected tab. | ❌ Windows has clickable/closable tabs only; `RefreshTabs` emits every tab in a `StackPanel`, with no drag reorder or overflow. | `Desktop/MainWindow.cs` | M | P2 |
| 1.4.1 | `1318f1e`, `6c3b9a5` — Ungroup Layers shortcut and context menu | Removes the folder, preserves child order, and releases invalid detached clips. | ✅ `LayerPlacement.Ungroup` promotes direct children in place, retains valid clipping stacks, and is wired to Layer menu, group context menu, undo/redo, and Ctrl+Shift+G. | `Core/Document/LayerPlacement.cs`, `Desktop/MainWindow.cs`, `Core/IO/Shortcuts.cs`, tests | M | P2 |
| 1.4.1 | `6955a6f` — Option-hover mask thumbnail cursor | Adds a duplicate-and-eye affordance before mask-alone click. | ❌ No mask-thumbnail hover/cursor behavior exists in the Avalonia layer list. | `Desktop/MainWindow.cs` | S | P3 |
| 1.4.1 | `686d8c7` — live transform fields commit as one undo | X/Y/W/H/scale controls preview then commit without a persistent Apply button. | ❌ The Windows move tool has canvas handles but no corresponding transform-value fields. | `Desktop/MainWindow.cs`, `Desktop/CanvasView.cs` | M | P2 |
| 1.4.1 | `c459f88` — resize-handle snapping | Resized edges snap to canvas/layer targets, respecting ratio/control modifiers. | ✅ Windows sends every transform draft, including resize, through `TransformEdits.Snap`; crop has its own snapping path. | `Core/Document/TransformEdits.cs`, `Core/Document/CropEdits.cs`, `Desktop/MainWindow.cs` | S | P2 |
| 1.4.2 | `7164dd4` — Liquify stays sharp | Uses source/displacement textures to avoid repeated resampling during a stroke. | ⚠️ C# uses a CPU local scratch buffer per dab; it is a different resampling model and has no equivalent upstream sharpness golden test. | `Core/Document/WarpEdits.cs`, `tests/Compositor.Core.Tests/WarpEditsTests.cs` | L | P2 |
| 1.4.2 | `ad9ad7c` — Smudge avoids ghost copies | Carries the prior dab’s pickup instead of repeatedly reusing stroke-start content. | ✅ C# updates its carried pickup after each dab and spaces smudge dabs continuously. | `Core/Document/WarpEdits.cs` | M | P2 |
| 1.4.2 | `504129d` — Blur Radius independent of Strength | Adds a dedicated Blur Radius setting and control. | ✅ `BrushSettings.BlurRadius`, tool options, and the blur kernel keep radius separate from opacity/strength. | `Core/Document/BrushEdits.cs`, `Desktop/ToolOptionsBar.cs`, `Desktop/MainWindow.cs` | S | P2 |
| 1.4.3 | `095da2f` — large-canvas Blur/Smudge/Liquify performance | Tiles/coarsens brush sources and replays sparse warp points rather than repeatedly processing full images. | ❌ Windows Blur/Clone samples and Warp allocate a full document surface; behavior exists but this large-canvas strategy does not. | `Core/Document/BrushEdits.cs`, `Core/Document/WarpEdits.cs` | L | P2 |
| 1.4.4 | `1aad384` — safe quit/close with pending edits | Applies pending gradient/pixel move and cancels dialogs/previews before close. | ✅ `Window.Closing` now uses a cancel-then-async-confirm bridge; it commits active gradient, floating pixels, text and transform, cancels crop/filter/Camera Raw/adjustment/colour-range/picker previews, and asks every changed tab Save / Don't Save / Cancel before disposal. Delayed previews are tab-owned and invalidated before bitmap disposal. Windows deliberately settles on tab switching (rather than upstream's v1.4.4 `canSwitch` block) to prevent cross-document state. Brush/shape/distort/guide drags still block until pointer release because final geometry is not safely inferable. | `Desktop/MainWindow.cs`, `Desktop/CanvasView.cs`, `Desktop/DocumentCloseFlow.cs`, `Desktop/UnsavedChangesDialog.cs` | M | P1 |
| 1.4.4 | `9c99853`, `cd2f998` — Dither > Scanlines (CRT), then parallel rendering | Adds CRT scanlines with line spacing, glow, dots, wobble, and parallel/cheaper render path. | ❌ Windows has the preceding ten Dither looks but no `Scanlines` enum/settings/UI/kernel. | `Core/Document/DitherEdits.cs`, `Core/Pixels/DitherPixels.cs`, `Desktop/DitherDialog.cs` | M | P2 |
| 1.4.5 | `60d9117` — Camera Raw point curve interaction and Parametric curve | Fixes gesture competition; point curves drag/add/delete with selection; parametric regions and dividers are interactive. | 🟡 Windows point curves already drag/add/right-click-delete through `CurveEditor`, so the original drag regression is absent. It lacks the Parametric page, tonal-region sliders, split dividers, selected-point treatment, and matching 16-point UX limit. | `Desktop/CurveEditor.cs`, `Desktop/CameraRawPanel.cs`, `Core/Document/CameraRawEdits.cs` | L | P1 |
| 1.4.5 | `60d9117` — Camera Raw Photoshop RGB tone semantics | Replaces kinked triangular parametric math with smooth gamma-bent anchors; applies composite tone curve per R/G/B; Refine Saturation negative returns toward luminance-only. | ⚠️ Windows still builds a luma LUT and uses the pre-1.4.5 Rec.709-luminance/`ScaleLuminance` path. | `Core/Document/CameraRawEdits.cs`, `Core/Pixels/AdjustPixels.cs` | L | P1 |

## Camera Raw: exact 1.4.5 gap

Only one substantive Camera Raw commit appears in this release interval:
`60d9117907704443b991d7855a3ea454a535ee6e`, immediately before the v1.4.5
release commit. Other Camera Raw groups (Light, Color, Effects, Detail, Optics,
Geometry, and Calibration) did not change in the upstream range.

The source changes are more specific than the release note:

1. The old parametric curve independently pushed a triangular tonal region.
   The replacement samples 33 anchors, applies two gamma-bend passes, then
   runs them through the existing smooth curve interpolation. It fixes endpoints,
   preserves monotonically rising output, and prevents divider kinks.
2. The old composite curve mapped Rec.709 luminance and scaled RGB back to that
   luminance. The new `toneLut` maps red, green, and blue independently. This
   means a normal S curve changes chroma as Photoshop does; `Refine Saturation =
   0` is the Photoshop-like default, negative values blend toward brightness-only,
   and positive values add chroma.
3. The SwiftUI graph now has one latched drag gesture. Point mode grabs/adds a
   point and constrains its neighbors; double-click removes it. Parametric mode
   adjusts a tonal region vertically or a divider along the lower graph edge.
4. Upstream added tests for smoothness/monotonicity, a Photoshop-traced
   Darks=-51/Lights=59 profile, and orange chroma under an S curve.

Windows already has a broad Camera Raw panel and live preview, but its
`CurveEditor` is a point-curves-only control. Its `CameraRawEdits.Curves()`
produces a luma LUT and `AdjustPixels.CameraRawCurveColor` follows the older
luminance-first behavior. This is a rendering difference, not simply a missing
control.

When porting this item:

- Keep the new settings **raw-only and transient**. Camera Raw is applied
  destructively to layer pixels and is not a `LayerAdjustment` stored in a
  `.comp` manifest.
- Do not reuse or change the persisted `Format.CurvesSettings` contract merely
  to represent Camera Raw’s parametric state.
- Add the upstream behavioral tests as C# tests, then add pointer tests for the
  Avalonia control.
- Add every new visible string to both
  `Desktop/Localization/Strings.resx` and
  `Desktop/Localization/Strings.zh-TW.resx`; use Taiwan terms such as
  `曲線`, `參數式`, `陰影`, `暗部`, `亮部`, and `亮部區域` as appropriate.

Existing Camera Raw gaps such as canvas-picked Point Color, auto/custom white
balance, targeted curve/mixer adjustment, and defringe sampling predate this
range. They are baseline gaps and should not be presented as v1.4.5 regressions.

## `.comp` compatibility

### Schema conclusion

There is **no `.comp` schema change from v1.3.7 through v1.4.5**.

- `git diff v1.3.7 v1.4.5 -- Compositor/IO/ProjectStore.swift`
  is empty.
- `git diff v1.3.7 v1.4.5 -- docs/project-format.md`
  is empty.
- Both tag sources declare `ProjectManifest.current = 11`, support versions
  `1...11`, and have the same `ProjectLayerRecord` properties.
- There is no new manifest property, serialized enum, asset layout, mask
  representation, or format-version bump in this release range.

Windows already writes/reads format 11 in
`Core/Format/ProjectManifest.cs`, `ProjectLayerRecord.cs`,
`ManifestJson.cs`, and `Core/IO/ProjectStore.cs`. Camera Raw and Dither in this
range are destructive/filter-session operations; their temporary settings are
not manifest fields.

### Important distinction: unchanged schema does not mean unchanged pixels

The following changes can alter render/import behavior for an existing project
or source file without requiring a migration:

| Source change | Compatibility implication | Windows action |
|---|---|---|
| `8c0417b` Add Noise origin and PSD decoding | Same `.comp` JSON; scaled adjustment output and PSD import can differ. | PSD/PSB now enters the Desktop document workflow with self-authored mask/clipping/blend fixtures. Keep the separate Add Noise positioning regression and obtain a macOS reference before declaring exact cross-platform parity. |
| `5a8f6ce` Soft Light | Same saved blend-mode enum; pixels can differ by implementation. | Compare Skia Soft Light to macOS/Photoshop fixtures; provide a custom formula if necessary. |
| `b3419ab` selection-derived masks | Same existing mask PNG and fields; creation behavior changes. | Implement the UI/tool semantics without changing mask serialization. |
| `60d9117` Camera Raw curves | Camera Raw result is baked into layer pixels; no manifest setting is introduced. | Port the transient algorithm only; do not add JSON fields. |

## Import, export, clipboard, and existing non-range gaps

The only import work changed by the upstream range is the PSD correctness work
in `8c0417b`. The Windows core parses PSD/PSB via `PsdImporter`, and Desktop now
routes those extensions before `ImageImporter.Decode`: an empty editor receives
an editable `CanvasDocument`; an existing document receives one wrapper group
whose children retain the imported hierarchy, masks, clipping and adjustments.
This does not add any `.comp` field or change format 11. A full localized
conversion-report/confirm sheet remains a follow-up: this batch reports only a
localized conversion-note count so Core's English free-form note text is never
shown in a Chinese UI.

Desktop does not yet have a file drag/drop entry point for any image format, so
there is no PSD-specific drop route to wire in this batch. A future drop handler
must share the same extension dispatch and wrapper-group operation rather than
calling `ImageImporter.Decode` for PSD/PSB.

No new PNG, JPEG, TIFF, RAW, clipboard, or export option was added by upstream
between these tags. The audit still records current Windows limitations for
future parity planning:

- Image import supports common raster formats, TIFF, HEIC/AVIF, SVG, RAW, and
  PSD/PSB. PSD/PSB retains the layers Core can represent instead of flattening.
- Export UI/core currently writes PNG and JPEG, not TIFF or PSD/PSB.
- Cut/Copy/Paste is an in-process `ClipboardImage`, not an OS clipboard bridge.
- Text format supports color/font runs in format 10/11, but the Desktop editor
  edits whole-style text rather than per-range rich text.

These are not falsely attributed to 1.4.3–1.4.5 changes.

## Apple-specific replacement map

| Upstream API or change | Why it changed upstream | Windows replacement / decision |
|---|---|---|
| AppKit and SwiftUI (`ProjectTabs`, `NativeLayerList`, `HeldModifiers`, close lifecycle, curve controls) | Native tabs, modifier tracking, menus, sheet behavior, and pointer gestures. | Avalonia controls, `Pointer*` events, `Window.Closing`, custom tab layout, and `StorageProvider`. Keep behavior in Desktop; do not leak AppKit concepts into Core. |
| Metal, QuartzCore, `GPUCanvas`, `GPUNoise`, `MetalWarp` | Interactive texture-resident canvas, GPU warps, texture lifetime/performance. | Keep the verified CPU/Skia renderer first. If profiling later justifies GPU work, isolate a rendering backend and evaluate Skia GPU surfaces/runtime effects; do not port `CAMetalLayer` or Metal command-buffer code. |
| CoreImage (`CISoftLightBlendMode`, Gaussian bloom, blur) | Photoshop-like Soft Light and Scanlines glow/filter composition. | Use Skia image filters where their pixels match; otherwise implement/test a CPU/Skia formula. Golden fixtures are required before switching behavior. |
| CoreGraphics | Adjustment surfaces, masks, PSD placement, geometry and brush rasters. | Existing `SKBitmap`, `SKCanvas`, `SKPath`, and `SKMatrix`; preserve alpha, coordinate-origin, and sampling rules in tests. |
| ImageIO / UniformTypeIdentifiers | Existing macOS package/image codecs and type registration; neither gained a range-specific change. | Existing `PngCodec`, Skia/ImageMagick/LibTiff/LibRaw decoders, extension lists, .NET paths, and Avalonia file picker types. |
| `dispatch_apply` in Scanlines | Parallel pixel preparation, line rendering, and glow composition. | `Parallel.For` or bounded partitioning after the sequential scanline kernel is correct and benchmarked. |
| Vision | No Vision source was added or changed in this version range. Pre-existing macOS subject-selection/background-removal features remain outside the port. | Do not add a model now. If this parity item is accepted later, evaluate ONNX Runtime plus a specifically licensed model, package size, offline behavior, and test fixtures. |

## Phased implementation plan

| Phase | Tasks | Likely files | Required tests | Risk / complexity |
|---|---|---|---|---|
| A — compatibility and correctness | Preserve format-11 round trips; keep the completed Desktop PSD/PSB routing and self-authored mask/clipping/blend/adjustment fixtures; add a localized conversion-confirmation dialog and macOS reference fixtures before declaring exact parity. | `Core/IO/PSD/*`, `Core/Document/LayerPlacement.cs`, `Desktop/MainWindow.cs`, optional import/conversion dialog, resources. | Existing manifest/project round trips; PSD/PSB fixture and wrapper-group history tests; Desktop resource contract; no manifest delta. | High data-correctness risk; M–L. |
| B — rendering behavior | Resolve Soft Light parity; compare adjustment/noise placement and clipping stacks; profile CPU tiles before considering a backend abstraction. | `Core/Pixels/BlendModes.cs`, `AdjustmentOperators.cs`, `Core/Rendering/DocumentRenderer.cs`. | Golden-pixel cases for blend modes, masks, adjustments, tiled-vs-whole render. | Rendering regressions; L. |
| C — Camera Raw parity | Add transient parametric curve state, smooth RGB tone LUT, Refine semantics, Parametric/Point UI, and localized labels. | `Core/Document/CameraRawEdits.cs`, `Core/Pixels/AdjustPixels.cs`, `Desktop/CameraRawPanel.cs`, `Desktop/CurveEditor.cs`, both resource files. | Upstream curve profiles, monotonicity, RGB/chroma fixtures, Avalonia pointer tests, `.comp` serialization unchanged. | Algorithm/UI coupling; L. |
| D — tools and editor UI | The first batch is complete: selection-aware masks and mask-alone view, Shift selected-pixel axis lock, ungroup, and safe close/settlement. Remaining work includes tabs overflow/reorder and transform fields. | `Core/Document/SelectionEdits.cs`, `LayerMaskEdits.cs`, `LayerPlacement.cs`, `Desktop/MainWindow.cs`, `CanvasView.cs`, tab controls, shortcuts/resources. | History/undo tests, layer-order/mask tests, keyboard and pointer desktop tests, close/cancel tests. | Interaction/history regressions; S–M per item. |
| E — Apple Vision replacements | Decide whether pre-existing subject/object/background selection merits a licensed ONNX-based replacement. | New opt-in integration only if approved; no change today. | Model-free interface tests; model/license acceptance tests if adopted. | Licensing/package/model quality; XL. |
| F — packaging and polish | Scanlines filter, parallelize only after golden output is stable, OS clipboard/export extensions, tab/mask cursor polish. | Dither core/dialog, clipboard/export services, Desktop resources. | Dither pixel fixtures and performance thresholds; end-to-end clipboard/export tests. | Platform integration and performance; M–L. |

### Recommended first implementation batch

This initial code batch was completed in `5488f20` and `e992d92`, without
changing renderer architecture or serialization:

1. [x] Normalize `Select All` followed by `Inverse` to no active selection.
2. [x] Add Reveal Selection / Hide Selection mask creation using the existing
   mask PNG representation.
3. [x] Add a view-only mask-alone preview with grayscale-render and non-mutating coverage.
4. [x] Add Shift axis-lock for selected-pixel dragging.
5. [x] Add Ungroup Layers with shortcut, context-menu, order, clipping, and
   undo/redo coverage.

Every visible label introduced by this batch must be added to both resource
files. Use localized display labels only; never change command IDs, enum values,
JSON keys, or `.comp` serialization strings.

### Pending-edit and safe-close batch

The v1.4.4 close lifecycle was completed without a format change:

1. [x] Settle active gradients, floating selected pixels, text, transforms, crop drafts, previews, Camera Raw, adjustments, Color Range, and the main color picker before a tab/document is disposed.
2. [x] Use an Avalonia cancel-then-async-confirm bridge for title-bar close and File → Exit; ask every changed tab in active-first order using Save / Don't Save / Cancel.
3. [x] Bind delayed preview work and color-range callbacks to their source tab so an old document cannot affect a newly selected one.
4. [x] Block brush, shape, distortion, and guide drags that lack enough final geometry to settle safely.

The Windows tab-switch settlement is intentionally more proactive than upstream
v1.4.4, which blocks `canSwitch` for most pending states. It prevents the
pre-existing Windows cross-tab preview/floating-pixel hazard while preserving the
same commit-versus-cancel semantics.

## Test checklist

- [x] Tag/source audit completed; format schema verified unchanged.
- [x] Add self-authored PSD/PSB fixtures for Levels/Curves/Hue, mask placement,
  clipping, representative blends, unsupported adjustment notes, and format-11 round trip.
- [ ] Add macOS/Photoshop reference pixels for Soft Light and scaled adjustment/noise;
  existing Add Noise positioning/tiled-render tests remain separate from PSD import.
- [ ] Add Camera Raw 1.4.5 curve math/chroma tests before UI work.
- [x] Add lifecycle regression coverage for clean/changed/save-failure/cancel close decisions,
  active-first multi-tab confirmation, nested history state, picker cancellation, stale-preview
  invalidation, and pointer-release-safe gradient settlement.
- [ ] Expand headless UI coverage for selection masks, Shift pixel movement,
  tabs, ungroup, and transform controls.
- [x] Run `dotnet test windows/tests/Compositor.Core.Tests/Compositor.Core.Tests.csproj`.
- [x] Run relevant Desktop/localization tests after visible UI changes.
- [x] Run `dotnet build windows/Compositor.slnx` with no new errors.

## Localization gate

The existing localization system is mandatory for all future Desktop work.
Every new user-facing string must have an English key in
`Desktop/Localization/Strings.resx` and a Taiwan Traditional Chinese value in
`Desktop/Localization/Strings.zh-TW.resx`. Keep internal enum values, shortcut
IDs, command IDs, JSON property names, and serialized `.comp` values stable.

## Progress checklist

- [x] Confirmed source tags and exact commit range.
- [x] Distinguished product version metadata from current Git ancestry.
- [x] Classified all 39 production-source commits in the range.
- [x] Confirmed no `.comp` schema/version change in the range.
- [x] Mapped Apple-only implementation choices to Windows equivalents.
- [x] Complete the first Phase D selection/mask/layer interaction batch.
- [x] Complete Phase A P0 PSD/PSB Desktop routing, wrapper-group handoff, and self-authored fixture coverage.
- [ ] Finish Phase A conversion-confirmation UI and macOS/Photoshop reference-pixel comparison.
- [ ] Complete Phase B rendering parity fixtures/fixes.
- [ ] Complete Phase C Camera Raw 1.4.5 parity.
- [ ] Complete Phase D interaction parity.
- [ ] Reassess Vision/ONNX only with explicit licensing and packaging approval.
