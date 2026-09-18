# Vendored licence records for the d1 fixtures

The four CSVs under `fixtures/data/` are redistributed here. `../fixtures/data/SOURCE.md` names each
one's upstream;
this directory carries the notice that upstream actually ships, fetched at a pinned revision so a
fresh checkout can audit the terms without a network call. It is a sibling of `fixtures/`, not a
child: only `fixtures/` is copied into a run workspace, and these files must not change what the
task sees.

| File | Covers | Fetched from | Pinned revision |
|---|---|---|---|
| `ggplot2-LICENSE`, `ggplot2-LICENSE.md` | `diamonds.csv` | `tidyverse/ggplot2` | `4e886647b2dbc3f41a1dd072c063ce0455671cc2` |
| `reshape2-LICENSE` | `tips.csv` | `cran/reshape2` | `82491189f8626ca2934e47f203b51d363b55baa9` |
| `palmerpenguins-LICENSE.md` | `penguins.csv` | `allisonhorst/palmerpenguins` | `8957207b78d6ccd1b4654a9dd9c9041b657478ab` |
| `R-COPYING-GPL-2` | `flights.csv` | `wch/r-source`, `doc/COPYING` | `cdb41eb32498185c88268ff90ee0d69e81dee9c0` |

Three things these files say that a licence label alone does not:

* **`reshape2-LICENSE` is a two-line stub**, and that is the whole of what upstream ships. A CRAN
  package declaring `MIT + file LICENSE` supplies only the year and copyright holder; the MIT text
  itself is CRAN's standard template, which those two fields complete. `ggplot2-LICENSE` is the same
  stub, and `ggplot2-LICENSE.md` beside it is that package's own copy of the full text — read them
  together to see what the stub stands for.
* **`R-COPYING-GPL-2` is GPL-2 only**, while R is offered as GPL-2 | GPL-3. The GPL-2 copy is the one
  R's own tree ships at `doc/COPYING`; the alternative is the standard GPL-3, unmodified.
* **A package licence is what covers these data files.** None of the four datasets carries a grant of
  its own, so the terms relied on are the containing package's, which cover its contents. That is an
  ordinary reading, but it is a reading, and it is recorded here rather than left implicit.

`flights.csv` is the only copyleft row. It is an inert 144-row fixture in an otherwise MIT
repository. Dropping it, or generating a substitute series, removes the question entirely at the cost
of changing what the d1 task measures.
