# Landing page

`site/` is the GitHub Pages landing page: one static page, no build step and no JavaScript.

| File | Purpose |
| --- | --- |
| `index.html` | The page. It makes no external requests (system font stacks, no web fonts). Facts on it mirror `README.md`, `docs/security.md`, `docs/deployment.md` (known limits), `evals/README.md` and the latest committed evidence folder. |
| `styles.css` | All styles. Colours are tokens on `:root`, redefined for dark mode under `prefers-color-scheme`; single column below 720 px. |
| `img/` | Images referenced by the page (see below). |
| `.nojekyll` | Serve the folder as is. |

`.github/workflows/pages.yml` deploys this folder on a push to `main` that touches `site/**`, or by hand (`workflow_dispatch`). The job runs only when the repository variable `PAGES_ENABLED` is `true` and Pages is set to deploy from GitHub Actions.

## Images

The images in `img/` are captures from the live evidence run [`docs/acceptance/2026-10-03-e7deee2`](../docs/acceptance/2026-10-03-e7deee2/summary.md), on synthetic test data. `img/layout-tod.png` is also the `og:image`. Keep each file under 500 KB and use only synthetic test data.

| File | Shown in | Source | Size |
| --- | --- | --- | --- |
| `img/layout-tod.png` | Live evidence; `og:image` | `docs/acceptance/2026-10-03-e7deee2/images/layout-tod.png` | 1200 x 776 |
| `img/layout-green.png` | Live evidence | `docs/acceptance/2026-10-03-e7deee2/images/layout-green.png` | 900 x 582 |
| `img/layout-mixed.png` | Live evidence | `docs/acceptance/2026-10-03-e7deee2/images/layout-mixed.png` | 1200 x 776 |
| `img/approval-card.png` | Safety model | Screenshot of the `arcpy.run-script` approval card in the dockpane during the same run's operation matrix | 501 x 390 |

The gallery crops to 1200:776, so a layout capture with a different aspect ratio is cropped, not distorted. If a replacement has different pixel dimensions, update the `width` and `height` attributes in `index.html` (and `og:image:width`/`og:image:height` for `layout-tod.png`) so the page does not shift while loading.

## When newer evidence lands

In `index.html`, in the `#evidence` section:

1. Point the evidence text at `docs/acceptance/<date>-<sha7>/` (the folder link, commit and ArcGIS Pro build).
2. Make the "Covered" and "Not covered" lists match that folder's `summary.md` and `operations/summary.json` (operations covered, cases passed, cards approved and denied), and keep the note on who decided the approval cards accurate for that run.
3. Recheck the test counts and eval numbers against `README.md` and `evals/README.md`.
4. If the run produced new layout captures, copy them into `img/` under the same names and update the table above.

## Preview locally

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory site
```

Then open `http://127.0.0.1:8765/`.
