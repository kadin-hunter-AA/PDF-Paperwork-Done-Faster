# Icon export acceptance review

Date: October 16, 2026

## Quantitative source and cleanup evidence

The immutable selected source is 1254 × 1254. Its alpha histogram contains 25,017 pixels with alpha 1–7, 2,449 with alpha 8–31, 4,158 with alpha 32–127, and 392,016 with alpha 128–254. An 8-connected component audit of the alpha 1–7 mask found 407 components: 67 touch pixels at alpha 8 or higher, while 340 are fully detached. All detached components are at most 47 pixels and total 763 pixels.

The exporter now clears only detached components under 8-connected alpha below 8 with up to 64 pixels. This removes 763 of 25,017 low-alpha pixels (3.0%) and preserves the 24,254 edge-connected low-alpha pixels, including broad antialiased edges. It changes alpha only; source colors and artwork geometry stay intact. The audit confirms 67 edge-connected components remain in the production master. The source PNG and historical production exports/ZIP were not changed.

## Export and visual evidence

`audit-icons.py` confirms all standalone PNGs have their declared square dimensions: 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 150 and 256 px. The ICO is 74,243 bytes and contains nine valid PNG-backed frames whose directory dimensions match their payloads: 16, 20, 24, 32, 40, 48, 64, 128 and 256 px. The audit checks reserved/type fields, planes=1, 32-bit color depth, in-file nonoverlapping payload ranges, and byte-for-byte equality with each corresponding standalone PNG.

The audit also asserts that every production-master RGB pixel equals the source RGB pixel and that the only alpha differences are the 763 eligible detached low-alpha pixels. It writes `icon-audit-metrics.json`, including per-asset alpha bounds/bands and SHA256 values for the selected source, production master, exporter, audit script, standalone PNGs and ICO. Running the exporter twice in one Windows PowerShell STA process completed successfully, confirming its type declaration is safe to reuse in that session.

The PNG alpha bounds show transparent margins of approximately 5–7% at common sizes, with small raster-rounding variation. The native-size review sheet at `icon-size-review.png` was checked against light Paper (#F5F6FA) and dark Ink (#171928) fields. The quill silhouette remains visible at 16 px; fine letter and nib details merge at the smallest sizes and are clearer by 32–48 px.

## Limits

This confirms deterministic asset cleanup, PNG dimensions, ICO frame structure and visual review of generated assets. It does not establish rendering by the installed Windows shell, clean-machine package acceptance, or final pixel-level identity acceptance.
