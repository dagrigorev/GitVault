# Icons

Material Symbols, licensed under the Apache License 2.0, fetched through the Iconify API by
`build/generate-icons.ps1`.

    https://github.com/google/material-design-icons
    Copyright the Material Symbols authors, licensed under Apache-2.0.

Apache-2.0 is one of the licences this project accepts. It permits redistribution in source and
binary form and imposes no attribution link inside the running application.

## Why not Icons8

Icons8 was the first choice. Two things ruled it out:

  * Its SVG endpoint requires a paid API key and answers HTTP 403 without one. Only the PNG
    endpoint is free, and raster icons cannot stay sharp across the 100-200% DPI range this UI
    targets.
  * The Icons8 free licence requires a visible attribution link inside the application and
    restricts redistribution of the icon files, an obligation every clone of this repository
    would inherit. That conflicts with the project's stated licence posture.

The .svg files here are kept for provenance; the application binds to the generated
`Icons.axaml`, not to the SVGs directly.