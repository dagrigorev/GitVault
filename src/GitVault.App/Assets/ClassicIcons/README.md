# Classic icon set

**Tango Icon Library 0.8.90 — public domain.**

The interface is styled after Win32-era administration utilities. Material Symbols, which the
first version of GitVault used, are thin monochrome outlines designed for modern mobile and web
surfaces; against classic chrome they read as a web application wearing a costume. Tango is the
icon language those utilities actually used.

## Licence

Public domain. `COPYING` in this directory is the upstream licence file, downloaded by the
generator alongside the artwork rather than transcribed, so the claim can be checked. Public
domain imposes no attribution obligation on this repository or on anyone who clones it, which
satisfies the project's licence rule (MIT / Apache-2.0 / BSD / MPL-2.0 / public domain only).

## Why rasters

Tango icons are multi-path artwork with gradients and highlights. They cannot be flattened into a
single `StreamGeometry` the way a Material Symbol can, and rendering SVG at runtime would mean
taking a new dependency. The generator downloads the 64px PNGs; the UI draws them at 16px in the
tree and menus and at 20px on the toolbar, with `HighQuality` interpolation. A 200% DPI display
therefore still has more source pixels than it needs.

## No network at runtime

The generator runs at development time. The PNGs are committed and shipped as embedded resources;
the application itself makes no network call, ever.

## Regenerating

```
pwsh build/generate-classic-icons.ps1
```

Edit the icon table at the top of the generator, run it, and commit both the PNGs and the
regenerated `Assets/ClassicIcons.axaml`. Do not edit the generated dictionary by hand.