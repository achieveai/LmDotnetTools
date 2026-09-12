# Additional extraction

Read the prepared complete history, extracted learnings and independent process assessment. Extract an additional reusable improvement to the review practice when evidence supports one. This question is workspace authored and may be changed without a C# DTO or host policy change.

Return structured Edits and Description in the same contract as the learnings step. Inspect existing KnowledgeBase entries before proposing replacements, preserve source attribution, and avoid duplicated knowledge. If the history supports no further edit, return an empty Edits array with a clear reason. Do not publish or change the source PR.

Use only KnowledgeBase/system/<slug>.md or KnowledgeBase/<trusted repository slug>/<slug>.md. Do not target developers, bookkeeping files, hidden names, or other repositories. Content must include the existing frontmatter title and matching scope. The host applies the accepted Edits during artifact retention and regenerates the index and table of contents; return the edits without writing files. Additional extraction must use paths distinct from the first extraction.
