# Self-hosted fonts & icons

`wwwroot/fonts/` ships the binary assets checked in; nothing loads from a CDN or
Google Fonts at runtime. This file documents what's here and how to refresh it.

## 1. Geist + Geist Mono (variable woff2)

```
wwwroot/fonts/Geist-Variable.woff2       (~29 KB)
wwwroot/fonts/GeistMono-Variable.woff2   (~23 KB)
```

Both are **variable fonts**: Google serves one physical file per family covering the
whole weight axis, and requesting weight 400 vs 500 resolves to the *same* URL — so
`tokens.css` declares a single `@font-face` per family with a weight **range**
(`font-weight: 400 500;`), not two separate static files. Only the `latin` Unicode
subset is included (this repo's UI copy is English-only, per `CLAUDE.md`); Cyrillic,
Vietnamese, and other subsets Google also offers were not fetched.

Geist is OFL-licensed (free for commercial use, self-hosting included). To refresh
or add a subset, request the Google Fonts CSS2 endpoint with a browser user agent
(a default `curl` user agent gets served ttf/woff instead of woff2) and pull the
`url(...)` from the matching `@font-face` block:

```bash
curl -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" \
  "https://fonts.googleapis.com/css2?family=Geist:wght@400;500&family=Geist+Mono:wght@400;500&display=swap"
```

## 2. Tabler Icons webfont (self-hosted)

```
wwwroot/css/tabler-icons.min.css
wwwroot/fonts/tabler-icons.woff2   (~890 KB)
wwwroot/fonts/tabler-icons.woff    (~1.2 MB, fallback for the rare browser without woff2)
```

`_Host.cshtml` links `_content/NeoReports.UI/css/tabler-icons.min.css`, whose
`@font-face` `url()`s are relative (`../fonts/tabler-icons.*`) to resolve correctly
once both files are RCL static web assets. The upstream package also ships a `.ttf`
fallback (adds ~2.5 MB); it's deliberately not included — woff2/woff already cover
every browser this design system targets.

To refresh to a newer Tabler release:

```bash
curl -o tabler-icons.min.css https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@<version>/dist/tabler-icons.min.css
curl -o tabler-icons.woff2   https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@<version>/dist/fonts/tabler-icons.woff2
curl -o tabler-icons.woff    https://cdn.jsdelivr.net/npm/@tabler/icons-webfont@<version>/dist/fonts/tabler-icons.woff
# then rewrite ./fonts/ -> ../fonts/ in the css and drop the .ttf src() entry
```

## Verifying self-containment

With no engine mounted, run the sample and check the browser's network panel (or
`preview_network` in this repo's tooling) — there should be zero requests to
`fonts.googleapis.com`, `fonts.gstatic.com`, or any CDN.
