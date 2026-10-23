# Scanlines (CRT) parity

Source reviewed: 2026-10-24. Target: macOS Compositor v1.4.4.

## Source and rendering contract

- [`9c99853cdf85309983e24cc6ef610cd99b0c0825`](https://github.com/robbietilton/Compositor/commit/9c99853cdf85309983e24cc6ef610cd99b0c0825)
  adds `DitherStyle.scanlines`, settings, full-resolution application, the CRT
  branch in `DitherPixels.c/.h`, UI gates in `FilterSheet.swift`, and four
  behavioral tests in `DitherTests.swift`.
- [`cd2f998440601cdcea4eb60bdc703181c5e09fec`](https://github.com/robbietilton/Compositor/commit/cd2f998440601cdcea4eb60bdc703181c5e09fec)
  renders independent line bands with `dispatch_apply` and reduces the glow
  before blurring. It preserves the CRT beam formulas.

Windows appends `Scanlines = 10`; styles 0 through 9 retain their values and
kernel paths. Pixel Size and Pixel Shape are bypassed for CRT, as for ASCII.
The native layer image is converted to premultiplied RGBA by `FilterSurface`.

For each column of each band, transparent samples are skipped. Straight RGB is
recovered from premultiplied input and density/contrast are applied before
averaging, in the same order as the C kernel. Original Colors averages three
adjusted color planes; the other modes average adjusted Rec.709 luminance and
interpolate the dark/light colors.

For spacing `s`, `middle = s / 2`:

```text
wave = sin(line * .45) * .7 + sin(line * 1.7 + 1.3) * .3
shift = roundAwayFromZero(wobble * wave)
along = (x + .5) % s - middle
sampleX = clamp(roundAwayFromZero(x - along * dots), 0, width - 1)
beam = middle * (.2 + .5 * sqrt(clamp01(tone)))
distance = sqrt((yInBand + .5 - middle)^2 + (along * dots)^2)
cover = clamp01(beam - distance + .5)
color = screen + (phosphorColor * 1.35 - screen) * cover
```

Wobble shifts the sampled source column; it does not move destination alpha.
Dots sample the color at each bead's center. Original Colors uses a black
screen; Two Colors uses the chosen dark color. RGB is clamped and premultiplied
with the original destination alpha. Alpha-zero kernel input is untouched.

## Glow and memory

The glow matches upstream's parameter mapping and composition:

```text
sigma = lineSpacing * 3 + 3
shrink = max(1, floor(sigma / 4))
smallSigma = sigma / shrink
resultRGB = min(originalAlpha,
                beamRGB + blurredRGB * (glow / 100 * 2.5) * originalAlpha / 255)
```

Windows area-averages premultiplied RGBA on a fixed shrink grid, including a
partial last cell, then uses the existing clamped separable Gaussian and
bilinear enlargement while composing. It never allocates a full-size bloom
bitmap or a float plane for every full-size image channel. The Gaussian's two
small float planes are allocated once and reused across channels. Alpha is
unchanged and the glow cannot write color into fully transparent destinations.

Independent bands use at most `min(32, processorCount, lineCount)` workers.
Each worker allocates one width-by-plane float buffer before rendering begins
and reuses it for all its bands. All columns of a band are averaged before its
rows are overwritten. No error-diffusion path is parallelized or changed.

Preflight estimates the work/output/thumbnail surfaces, optional original copy
and selection coverage, worker buffers, and reduced glow planes. New working
memory must fit both 512 MiB and one quarter of the runtime's available memory.
Invalid surface sizes and overflow are rejected before allocation. This limit
covers new filter working storage; existing document assets/history are already
owned by the editor and are not counted again as new allocations.

The formal layer is replaced only after beam rendering, glow, and selection
mixing succeed. `FilterSurface.TryKeep` reports selection allocation failure,
so CRT refuses instead of applying outside the selection. Allocation refusal
leaves pixels, placement, dirty state, and undo count unchanged. The status bar
reports failure through resources.

## UI and localization

The existing Dither dialog shows Line Spacing (2-32 px), Glow (0-100%), Dots
(0-100%), Wobble (0-64 px), Density, Contrast, and Colors for CRT. Inapplicable
pixel, halftone, ASCII, diffusion, tone-count and mark-polarity controls hide.
Defaults are spacing 4, glow 35%, dots 0%, wobble 0 px. Reset retains Dither's
existing all-settings contract, including resetting the style to Atkinson.
Apply/Cancel and remembered settings use the existing dialog flow.

Preview callbacks receive independent settings copies. Style/color changes
refresh the preview, and Cancel cannot mutate remembered settings. The existing
tab-owned preview timer/settlement code is unchanged.

New keys in both resources:

| Key | English | Traditional Chinese (Taiwan) |
|---|---|---|
| `Dither.Style.Scanlines` | Scanlines (CRT) | 掃描線（CRT） |
| `Dither.LineSpacing` | Line Spacing | 掃描線間距 |
| `Dither.Glow` | Glow | 光暈 |
| `Dither.Dots` | Dots | 光點 |
| `Dither.Wobble` | Wobble | 水平波動 |
| `Status.DitherStyleApplied` | Dither: {0} | 已套用網點：{0} |
| `Status.DitherFailed` | Unable to apply Dither. The image may exceed the working memory limit. | 無法套用網點濾鏡，影像可能超出工作記憶體上限。 |

Resource key sets are identical: 1168 en-US, 1168 zh-TW.

## Reference fixtures and validation

`Fixtures/dither-upstream.json` was generated by compiling the **unmodified**
`DitherPixels.c` and `.h` at `9c99853` with the retained
`Fixtures/scanlines-reference.c` harness using GCC `-O2 -std=c99 -lm`.
The harness defines the exact 23x19 premultiplied input, partial/zero alpha,
settings, colors, and synthetic ASCII glyph maps. It emits RGBA bytes for all
eleven styles, independently of the Windows implementation. To reproduce,
export the two files from that commit beside the harness, compile the two `.c`
files with the header available, and run the executable. The colon-separated output is stored in JSON as
style indices plus hex RGBA. Ordinary .NET tests consume the checked-in fixture
and do not require a C compiler.

`ScanlinesTests` checks every byte within one level for cross-runtime libm
rounding. It also covers upstream's white/gray/black, glow-gap, periodic bead
and wobble-edge cases, a separate double-precision beam oracle, Original/Two
Colors, alpha, partial bands, one-pixel dimensions, row padding, ignored
settings, sequential/parallel equality, bounded-memory refusal, selection
clipping, preview cancellation, one-step undo/redo and `.comp` PNG round-trip.
`ScanlinesDialogTests` checks both languages, visible rows, Apply/Cancel,
Reset, and preview settings ownership on Avalonia Headless.

Local verification: Core 981 passed, Desktop 35 passed; solution build with
`-warnaserror --nologo` produced zero warnings and zero errors.

## Compatibility and remaining differences

Scanlines is destructive and stores ordinary raster pixels. Format version
remains **11**. No manifest fields, serialized enum values, filter records,
mask encoding, keyboard identifiers, or import behavior are changed.

The C fixtures validate the upstream **beam kernel**, not a macOS application
binary or Core Image glow output. Windows uses area reduction and bilinear
enlargement; Core Image chooses its own reconstruction kernel. The Gaussian
radius and premultiplied additive equation match, but the glow is not claimed
byte-identical at resampling edges. No macOS binary glow fixture is available.
Windows also streams band tone preparation instead of allocating upstream's
full-image tone/source arrays. Refusing operations beyond its explicit memory
budget is an intentional platform safety difference.
