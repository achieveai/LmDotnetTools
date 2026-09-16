# Fixture provenance

`docs/report-01.txt` .. `report-14.txt` and `stopwords.txt` are synthetic, generated once by
`../hidden/gen_docs.py` (seeded, deterministic; the same run writes `../hidden/counts.json`, the
non-stop-word counts per document, and `../hidden/stations.json`, the station id and verified sensor
count per document). No external data. Regenerate only together with the hidden keys.
