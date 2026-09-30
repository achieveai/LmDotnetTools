# Source

The articles under `docs/` are context paragraphs from the HotpotQA distractor-setting dev set,
regrouped: every paragraph with the same title is one file.

- Dataset: HotpotQA — Yang, Qi, Zhang, Bengio, Cohen, Salakhutdinov, Manning, "HotpotQA: A Dataset
  for Diverse, Explainable Multi-hop Question Answering", EMNLP 2018. https://hotpotqa.github.io/
- File: `distractor/validation-00000-of-00001.parquet`, fetched 2026-09-24 from
  https://huggingface.co/datasets/hotpotqa/hotpot_qa at revision `1908d6afbbead072334abe2965f91bd2709910ab`
  (the official `hotpot_dev_distractor_v1.json` URL did not answer on 2026-09-24).
  sha256 `c20b638ca82b21d04fe12e14ff417ad05153d4d215a65de54497fca4e972f7c6`, 27,452,575 bytes.
- Licence: CC BY-SA 4.0 (dataset card, "Licensing Information"; the full text is beside this task in
  `licenses/`). This derived subset is distributed under the same licence. The paragraphs are
  Wikipedia text (CC BY-SA).
- Changes: subset selection, sentences joined into one paragraph, grouping by title, file naming.
  No text was edited.
