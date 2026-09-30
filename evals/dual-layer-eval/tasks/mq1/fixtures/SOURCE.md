# Source

The articles under `docs/` are paragraphs from the MuSiQue dataset (answerable split, dev set),
regrouped: every paragraph with the same title is one file, paragraph order inside a file is shuffled.

- Dataset: MuSiQue v1.0 — Trivedi, Balasubramanian, Khot, Sabharwal, "MuSiQue: Multihop Questions via
  Single-hop Question Composition", TACL 2022. https://github.com/StonyBrookNLP/musique
- File: `musique_ans_v1.0_dev.jsonl`, fetched 2026-09-24 from the Hugging Face mirror
  https://huggingface.co/datasets/bdsaglam/musique at revision `22873a405dd809893b22ada0b499299fb612d2df`
  (the official release is a Google Drive zip linked from the GitHub README).
  sha256 `15fa63794d18a94ce12411aca6e2327e65b6e83b0b1490efab3f1962e48abf3b`, 30,439,728 bytes.
- Licence: CC BY 4.0 (upstream `LICENSE` at `StonyBrookNLP/musique@922ac98f19a201998dbdae6d7f2887a5258dbdeb`;
  a copy sits beside this task in `licenses/`). The paragraphs are Wikipedia text (CC BY-SA).
- Changes: subset selection, grouping by title, file naming, paragraph shuffling. No text was edited.
