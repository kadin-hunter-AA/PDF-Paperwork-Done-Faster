# Windows icon delivery

Selected purple/gold PDF-letter quill, October 12, 2026.

- HunterOPS-PDF.ico: 32-bit PNG-compressed frames at 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels.
- icon-master-1024.png: padded transparent raster master.
- png/: exact-size standalone assets, 16 through 256 pixels.
- msix/: default, dark-unplated and light-unplated target-size assets plus base/scale assets for AppList (44), Square150x150Logo and StoreLogo.
- small.png: simplified optical source for exports through 48 px; master.png supplies larger assets.
- size-preview.png: light/dark review at actual common pixel sizes.
- verification.json: PNG dimension/alpha and ICO frame validation evidence.
- export-icons.cjs: reproducible packaging from the two raster sources using sharp.

Packaging follows https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction (checked October 12, 2026). Same artwork is intentionally used for light/dark unplated variants, after reviewing both backgrounds. The official HUNTER OPS company logo is unaffected.

For WPF: copy HunterOPS-PDF.ico into the application's Assets directory; set ApplicationIcon to Assets\HunterOPS-PDF.ico in the project and use the same resource for the window Icon. Build/package must then verify title-bar, taskbar, Explorer and installer behavior.

For MSIX: copy msix contents to package Assets. Point Square44x44Logo to Assets\AppList.png, Square150x150Logo to Assets\Square150x150Logo.png and the package Properties Logo to Assets\StoreLogo.png. Package identity, display name, executable and entry point belong to the real app manifest and are not invented here.

Raster delivery, not editable vector artwork. PNG pixels contain tonal shading from the selected metallic design; exact flat-color brand tokens still govern GUI controls. At 16–24 pixels the silhouette remains recognizable, but PDF lettering and nib engravings cannot be read as clearly as at large sizes.

Assets are delivered in this workspace. They are not wired into or installed with the application: the current workspace contains no app project, and the separate implementation mirror prohibits synced-file changes. App integration and installed-shell checks need a writable app checkout.

Cleanup status: built-in image_gen produced detailed and simplified masters, but two cleanup passes retained colored fringe at full master resolution. Normal-size ICO/PNG previews were reviewed on light/dark backgrounds; masters are not certified fringe-free. Pixel-level cleanup approval is pending. Do not label this package fully production-approved until that cleanup completes. Windows System.Drawing successfully decoded the ICO at 32 px; all nine embedded PNG frame sizes independently decoded and all 84 asset PNG dimensions/alpha passed validation.
