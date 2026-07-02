# Self-hosted fonts & icons

The design tool that produced this starter **cannot ship binary font files**, so
`wwwroot/fonts/` needs three sets of assets fetched once. After that everything is
fully offline — no Google/CDN calls.

## 1. Geist + Geist Mono (woff2)

`tokens.css` already declares `@font-face` for these exact filenames:

```
wwwroot/fonts/Geist-Regular.woff2
wwwroot/fonts/Geist-Medium.woff2
wwwroot/fonts/GeistMono-Regular.woff2
wwwroot/fonts/GeistMono-Medium.woff2
```

Geist is OFL-licensed (free for commercial use). Get the woff2 files from either:

- **Vercel Geist** repo: https://github.com/vercel/geist-font → `packages/next/dist/fonts/`
- **Google Fonts**: https://fonts.google.com/specimen/Geist and https://fonts.google.com/specimen/Geist+Mono (download → convert ttf→woff2 with `woff2_compress` or https://cloudconvert.com)
- **Fontsource** (easiest, already woff2):
  ```bash
  npm i @fontsource/geist-sans @fontsource/geist-mono
  # copy the 400 + 500 weight woff2 files from node_modules/@fontsource/...
  ```

Only weights **400** and **500** are used — don't ship the rest.

## 2. Tabler Icons webfont (self-host)

`_Host.cshtml` currently links Tabler from jsDelivr CDN. For production, self-host:

```bash
npm i @tabler/icons-webfont@3.30.0
# copy from node_modules/@tabler/icons-webfont/dist/:
#   tabler-icons.min.css        -> wwwroot/css/tabler-icons.min.css
#   fonts/tabler-icons.woff2    -> wwwroot/fonts/tabler-icons.woff2  (+ .woff, .ttf)
```

Then in `_Host.cshtml` replace the CDN `<link>` with:

```html
<link rel="stylesheet" href="css/tabler-icons.min.css" />
```

(The `.min.css` references `./fonts/tabler-icons.*` relative to itself — keep the
`fonts/` folder next to the css, or edit the `src:` url in the css to point at
`../fonts/`.)

## Why this isn't automated

Font binaries are large and license-bound; the design environment can't fetch or
emit them. Everything else in this project is text and ships as-is. Once these
files are dropped in, delete the CDN `<link>` and the app is 100% self-contained.
