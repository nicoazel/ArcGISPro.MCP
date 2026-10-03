# Landing page

`site/` is the GitHub Pages landing page: one static page, no build step and no JavaScript.

| File | Purpose |
| --- | --- |
| `index.html` | The page. Facts on it mirror `README.md`, `docs/security.md`, `docs/deployment.md` (known limits), `evals/README.md` and the latest committed evidence folder. |
| `styles.css` | All styles. Colours are tokens on `:root`, redefined for dark mode under `prefers-color-scheme`; single column below 720 px. |
| `img/` | Images referenced by the page (see below). |
| `.nojekyll` | Serve the folder as is. |

`.github/workflows/pages.yml` deploys this folder on a push to `main` that touches `site/**`, or by hand (`workflow_dispatch`). The job runs only when the repository variable `PAGES_ENABLED` is `true` and Pages is set to deploy from GitHub Actions.

## Images

The images in `img/` are placeholders until the next live evidence run. Replace each file with a PNG of the same name; the page needs no other change. Keep each file under 500 KB and use only synthetic test data.

| File | Shown in | Source | Aspect |
| --- | --- | --- | --- |
| `img/layout-tod.png` | Live evidence; also the `og:image` | `docs/acceptance/<new>/images/layout-tod.png` | 1200 x 776 |
| `img/layout-green.png` | Live evidence | `docs/acceptance/<new>/images/layout-green.png` | 1200 x 776 |
| `img/layout-mixed.png` | Live evidence | `docs/acceptance/<new>/images/layout-mixed.png` | 1200 x 776 |
| `img/approval-card.png` | Safety model | Screenshot of one approval card in the MCP Studio dockpane | 720 x 900 (portrait; other sizes work, update `width`/`height` on the `<img>`) |

The gallery crops to 1200:776, so a layout capture with a different aspect ratio is cropped, not distorted. If a replacement has different pixel dimensions, update the `width` and `height` attributes in `index.html` so the page does not shift while loading.

## When the new evidence lands

In `index.html`, search for `EVIDENCE:` and:

1. Point the evidence text at `docs/acceptance/<date>-<sha7>/` (the folder link and, if wanted, each image link).
2. Make the "Covered" and "Not covered" lists match that folder's `summary.md`.
3. Recheck the test counts and eval numbers against `README.md` and `evals/README.md`.

## Preview locally

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory site
```

Then open `http://127.0.0.1:8765/`.
