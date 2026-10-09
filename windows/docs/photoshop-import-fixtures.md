# Photoshop import fixtures

The PSD and PSB fixtures used by the Windows Core tests are deliberately tiny, deterministic files written
by `windows/tests/Compositor.Core.Tests/PsdImportTests.cs`. They are generated into the test's temporary
directory rather than checked in as opaque binary blobs, so every byte is reviewable beside the expected
pixel assertions.

They contain only project-authored solid colours, one-pixel geometry, layer names and mask values. No Adobe,
Photoshop, macOS, or third-party artwork or sample document is included or redistributed.

| Generated fixture | Format | Coverage |
| --- | --- | --- |
| `Layered.psd` | PSD v1 | Basic layers, visibility, opacity/fill, Unicode name, folder, partial layer mask, clipping and raw/PackBits channels. |
| `Layered.psb` | PSB v2 | The same small semantic document through the v2 length and RLE-row paths, with a real `.psb` extension. |
| `Blend-modes.psd` | PSD v1 | Normal, Multiply, Screen, Overlay and Soft Light keys, plus rendered reference pixels. |
| `Adjustments-supported.psd` | PSD v1 | Editable Curves and Hue/Saturation records. Levels has its own `Levels.psd` fixture. |
| `Adjustments-unsupported.psd` | PSD v1 | Exposure, Black & White and Color Balance records. The test asserts that the importer reports and skips them rather than claiming editable support. |

`AnImportSavesAndRendersThroughTheProjectFormat` is the end-to-end golden path for the mask and clipping
fixture: PSD bytes → `PsdImporter` → `ProjectSnapshot` → `.comp` save/load → `CanvasDocument` → renderer.
`PhotoshopBlendFixtureMapsAndRendersRepresentativeModes` performs the corresponding pixel-level blend check.

The fixture builders are intentionally not an assertion that all Photoshop rendering is identical. In
particular, Soft Light checks the standard blend equation at fixed opaque sample values; it is a regression
guard for the Windows mapping and renderer, not a substitute for a Photoshop-produced visual oracle.

Add Noise is not a PSD adjustment record handled by this importer. Its deterministic positioning coverage
remains in the independent Core noise and tiled-render tests, where a fixed seed verifies that a tiled piece
has the same document-space pattern as a whole render.
