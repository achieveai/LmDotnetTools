# Source

All four files are verbatim copies from https://github.com/mwaskom/seaborn-data at commit
`71e2436a092d714350de0fc409ca8a8714e7e78f` (fetched 2026-09-16): `diamonds.csv`, `penguins.csv`,
`flights.csv`, `tips.csv`.

## Redistribution

That mirror is a convenience copy and grants nothing on its own: it carries no `LICENSE` file, and
GitHub's licence endpoint for it returns 404 (checked 2026-09-17). So the right to redistribute each
file here is traced to the dataset's own upstream, one row per file:

| File | Upstream | Licence | Checked |
|---|---|---|---|
| `penguins.csv` | [allisonhorst/palmerpenguins](https://github.com/allisonhorst/palmerpenguins) — Palmer Station LTER, Gorman et al. | CC0-1.0 (`LICENSE.md`) | 2026-09-17 |
| `diamonds.csv` | [tidyverse/ggplot2](https://github.com/tidyverse/ggplot2), the `diamonds` dataset (`R/data.R`) | MIT (`DESCRIPTION`: `MIT + file LICENSE`) | 2026-09-17 |
| `tips.csv` | [reshape2](https://github.com/cran/reshape2), the `tips` dataset; originally Bryant & Smith, *Practical Data Analysis* (1995) | MIT (`DESCRIPTION`: `MIT + file LICENSE`) | 2026-09-17 |
| `flights.csv` | The Box & Jenkins series G (`AirPassengers`), shipped in R's base `datasets` package | GPL-2 \| GPL-3, R's own (https://www.r-project.org/Licenses/) | 2026-09-17 |

Two things this table states and one it does not:

* Each licence is the one the containing R package declares. A package licence covers the files in
  the package, data included; none of these four datasets carries a separate per-file grant. That is
  the reading this repository relies on, not a licence granted file by file.
* `flights.csv` is the one copyleft row. It is 144 rows of a published time series used as an inert
  test fixture — never compiled, linked, or distributed as part of a package — but it is GPL where
  the rest of this repository is MIT, so it is called out rather than left to be discovered.
* Identity was checked against the upstream description, not merely assumed from the filename:
  `flights.csv` runs 1949-01 = 112 through 1960-12 = 432, which is series G.

Replacing any of these with generated data would end the question entirely; it would also change what
the d1 task measures, which is why it was not done here.
