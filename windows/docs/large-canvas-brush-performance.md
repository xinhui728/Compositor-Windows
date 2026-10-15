# Large-canvas brush performance parity

This record tracks the Windows implementation of the large-canvas brush work
introduced upstream in
[`095da2fb0e4d08ee4e368e7b2c4a0299a7c06b6f`](https://github.com/robbietilton/Compositor/commit/095da2fb0e4d08ee4e368e7b2c4a0299a7c06b6f),
“Blur, Smudge and Liquify keep up on big canvases.” It is intentionally a
performance/correctness contract rather than a wall-clock benchmark. Hardware,
Skia allocation behaviour, and CI load vary; allocation bounds, evaluated
regions, pixel results, and ownership do not.

Last reviewed: 2026-10-15

## Upstream evidence

The upstream commit changes five production/test files:

| Upstream file | Observable change |
|---|---|
| `Document/BlurTool.swift` | Keeps the sharp source and exposes a `render(part)` closure. Core Image produces only the softened piece requested by the brush instead of materialising the whole blurred source before the first dab. |
| `Document/BrushStroke.swift` | Caches the cropped source piece for each touched paint tile; a two-pixel interpolation margin preserves the result at a tile edge. It also raises the internal maximum diameter to 2100 for app-generated replay strokes. |
| `Document/SmudgeLiquify.swift` | Replays only spatially meaningful warp points on release. Intermediate points closer than five percent of brush diameter are skipped, while the final point is retained. |
| `Document/EditorSession+Brush.swift` | Uses the new deferred Blur source when committing a brush stroke. |
| `CompositorTests/LargeCanvasBrushTests.swift` | Exercises Blur, Smudge, and Liquify at a 2000px brush on a 5000px canvas and verifies piece rendering agrees with a whole blur to within one 8-bit level. |

The relevant upstream Blur facts are preserved in the Windows design:

1. The source is a snapshot from stroke start, so a new stroke can soften a
   prior result but a dab cannot sample pixels written by an earlier dab in the
   same stroke.
2. Sigma is converted from canvas/document units to the source grid and a
   three-sigma source halo is required around the requested output.
3. A single scale factor is selected for a stroke before pieces are made. This
   prevents neighbouring pieces from using different sampling grids.
4. The result is not required to allocate or render a document-sized Current
   Layer bitmap. `Sample All Layers` remains a document-composite snapshot by
   design because it means “what is visible on the canvas.”

The Swift implementation relies on Core Image's deferred evaluation; the C#
implementation cannot assume that a Skia image filter will remain deferred.
It therefore owns the equivalent explicit, bounded tiles described below.

## Windows architecture

### Coordinate ownership and snapshot lifetime

`LayerPixelSpace` is the boundary between document space and a layer/mask's
native pixel grid. Pointer paths, brush radius, and layer placement begin in
document space. Current Layer Clone/Blur and mask Blur map them to their
respective native grids. This preserves rotation, flip, translation, and
non-uniform scaling without a document-sized Current Layer raster.

The native source bitmap is borrowed read-only during the edit. A separate
`painted` bitmap receives output and replaces the layer only after the complete
operation succeeds. Each cached Blur piece owns its `SKBitmap`, is disposed on
eviction or stroke completion, and never owns or disposes the layer's source
asset. A failure to prepare a later piece rejects the entire edit; it must not
leave a partially blurred layer, history entry, or stale source reference.

### Lazy native-grid Blur

`NativeBlurTileSample` is the lazy Current Layer source for color and Gray8
mask Blur. It has these deliberate bounds:

| Limit | Windows value | Purpose |
|---|---:|---|
| Paint-tile side | 256 native pixels | Matches the practical tile granularity used by the C# brush path and limits source work to inked pieces. |
| Halo | `ceil(3 × sigma × fit) + 2` output pixels | Three Gaussian sigmas plus interpolation room; the tile centre has the same input neighbourhood as a whole blur. |
| Stroke planning budget | `min(MaxSurfacePixels, max(16M, 4 × canvasPixels))` | Selects one coarsening factor for the whole stroke, following the upstream budget shape. |
| Individual tile ceiling | 1,000,000 pixels | Stops a severely scaled-down layer from making a single giant halo allocation. |
| Cached pieces | 8 FIFO tiles | Bounds retained source bitmaps during a long stroke. Brush application processes coverage tile-by-tile, so a committed stroke does not rebuild a previously processed tile. |
| Runtime allocation guard | At most one quarter of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` | Refuses impractical allocation before it can turn into an out-of-memory/UI-freeze path. |

The stroke-wide fit is the minimum of the stroke and tile budgets. Every tile
therefore maps to the same downsampled source coordinate system. It is filled
from the immutable native snapshot, blurred once, cached, and sampled only
where coverage is non-zero. The tile's halo is never copied into the document:
only covered destination pixels are written.

Color tiles retain the existing straight-versus-premultiplied conversion:
source layer pixels are read as straight RGBA, converted to premultiplied for
the Gaussian pass, then converted back through the existing blend path. Gray8
mask tiles use `LayerMask.Background` outside the mask instead of transparent
black, including unlinked mask placement. This preserves reveal/hide edge
semantics and avoids alpha halos.

### Diagnostics and deterministic limits

`NativeBrushSamplingDiagnostics` is internal/test-only observability. It
records deterministic planning facts rather than making CI depend on elapsed
milliseconds:

- tiles requested and tiles actually built;
- blur computations;
- largest temporary piece and estimated peak working bytes;
- whether coarsening was used; and
- the selected paint-tile size.

The diagnostic accounting includes the cached bitmap and the two float planes
owned by `GaussianBlur` while a piece is evaluated. It is useful for a local
benchmark report (stroke duration, preparation time, tile count, allocations),
but elapsed duration is deliberately diagnostic-only. CI asserts explicit ROI,
tile, and allocation ceilings instead.

## Smudge and Liquify ROI implementation

Unlike Blur, Smudge and Liquify are sequential warps: later dabs intentionally
observe the earlier stroke state. A naive document-sized work bitmap plus a
full-layer readback is not suitable for large canvases.

The Windows CPU path now uses a bounded document-space ROI rather than a full
document bitmap. It is deliberately an incremental equivalent of the upstream
release-time improvement, not an attempt to reproduce Metal's texture pipeline:

1. Preflight the existing continuous dab path and falloff before allocating a
   bitmap; the actual Warp dabs are not decimated, so current Windows pixel
   behaviour remains stable.
2. Union each dab's write disk and Liquify read margin into one document ROI.
   The layer is rendered once into that ROI in document coordinates, preserving
   rotation, flips, scaling, alpha, and layer sampling.
3. Rent one bounded carried-colour buffer for Smudge and one byte snapshot
   buffer for Liquify. Liquify reads the snapshot before each dab writes;
   Smudge retains and updates its carried pickup in the established order.
4. Map only the affected document rectangle back into the layer's native grid.
   A spatial index of dab centres makes coverage testing proportional to nearby
   dabs rather than all dabs in the stroke. Unreached native pixels remain
   byte-for-byte untouched.
5. Reject a plan over the working/scratch allocation caps before assigning a
   new layer asset. All temporary bitmaps and pooled buffers are released in
   `finally`/`Dispose` paths.

This is intentionally not a claim that the Windows CPU/Skia algorithm is
binary-identical to Metal. Required observable invariants are continuous
coverage, the existing hardness/strength curves, carried-color Smudge pickup,
Liquify's pre-dab sampling, one undo snapshot per completed stroke, and
unchanged selection/transform semantics.

## Pixel and regression strategy

The suite separates exact cases from controlled approximations.

| Fixture class | Assertion |
|---|---|
| Native, uncoarsened Blur | Compare covered pixels with an independent whole-native Gaussian oracle. Max RGBA absolute error is at most one level; verify a tile boundary and pixels outside coverage. |
| Tile boundaries | Exercise one stroke across at least one 256px boundary. The two sides must agree with the whole oracle and must not write the blur halo itself. |
| Coarsened huge virtual ROI | Assert arithmetic/working-memory ceilings without allocating a multi-gigabyte bitmap. Record that coarsening occurred. |
| Transformed layer/mask | Verify rotated, flipped, scaled, and non-uniform placement maps to native pixels and mask background semantics remain intact. |
| Snapshot/undo | A later stroke may blur a prior result; within one stroke, source pixels remain the start snapshot. Undo/redo and project save/load preserve the result. |
| Warp | Cover short/long paths, repeated dabs, tile boundaries, transparent pixels, and layer transforms. Assert only touched native pixels change and that Smudge/Liquify preserve their existing mathematical oracle. |

For intentionally coarsened results, compare maximum absolute channel error,
mean absolute error, alpha error, and the tile-edge difference against a
full-resolution analytical/reference render. Do not present a coarsened result
as byte-identical. Full-resolution native cases should remain byte-exact where
the operation permits it, or use the documented one-level Gaussian tolerance.

The project-format tests remain part of this gate: a brush edit must survive
`.comp` save/load unchanged and **must not** alter format version 11, manifest
schema, JSON keys, serialized enums, mask encoding, or asset naming.

## Benchmark protocol

Local, non-gating benchmark runs should report a fixed fixture's:

- total stroke time;
- source-preparation and per-tile time where profiling is available;
- allocated bytes/GC collections and estimated peak working bytes;
- tiles requested/built/evicted and Gaussian computations; and
- output error versus the whole/native oracle.

Use a 5000px canvas with a 2000px brush for parity with the upstream stress
case, plus smaller transformed/masked fixtures. Warm-up, machine details, and
the exact fixture seed must be recorded. A benchmark regression is an
investigation signal, not a flaky fixed-time CI failure.

## Known differences and follow-up

- Windows uses explicit CPU/Skia tiles; macOS delegates deferred piece
  evaluation to Core Image. The two implementations share source/halo/snapshot
  semantics but are not yet backed by cross-platform binary pixel fixtures.
- The Windows tile cache is bounded FIFO. Upstream retains every piece for the
  `BrushStroke` lifetime. Windows visits coverage by tile, so normal commit
  order avoids recomputation; a future live-preview path must preserve that
  property if it can revisit an evicted tile.
- The current task does not introduce a GPU backend, modify Camera Raw, change
  PSD/PSB import, or change `.comp` serialization.
- Windows uses one bounded ROI rather than Metal's persistent textures. It has
  deterministic ROI/scratch diagnostics and behaviour fixtures, but no macOS
  binary-pixel or interactive-frame benchmark fixture yet. Do not relax
  carried-colour or pre-dab sampling merely to reduce memory use.

## Completion checklist

- [x] Lazy color and mask Blur evaluate only touched native-grid tiles.
- [x] Tile halo/coarsening/cache limits have deterministic regression coverage.
- [x] Current Layer and Sample All Layers retain their intentionally distinct
  source spaces.
- [x] Smudge/Liquify avoid document-sized working copies and per-dab large
  allocations while preserving existing stroke semantics.
- [x] Native/transformed/masked/transparent/project round-trip fixtures
  pass.
- [ ] Core and Desktop tests, warning-as-error build, and relevant Actions jobs
  are green.
