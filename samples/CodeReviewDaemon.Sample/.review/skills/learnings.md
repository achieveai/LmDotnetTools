# Extract durable learnings

Read the complete prepared PR history and the current KnowledgeBase index, table of contents and relevant entries. Identify durable, evidence-backed lessons about contracts, architecture or failure modes. Avoid duplicating existing knowledge and do not use prose keyword ranking to select context.

Return structured Edits and a Description. Each edit names a relative KnowledgeBase path and its complete replacement Content. Use the existing entry format and preserve useful current information. Names, dates and source references must come from trusted prepared context. Do not invent author identities or fabricate attribution. Avoid private cross-repository facts outside the authorized retention scope. If no durable lesson exists, return an empty Edits array with an explicit reason.

The retention script owns containment, encoding, bounded IO, ledger updates and index regeneration. Do not execute arbitrary writes, pushes or source-PR actions. Final output must match the supplied JSON schema exactly.

Use only KnowledgeBase/system/<slug>.md or KnowledgeBase/<trusted repository slug>/<slug>.md. Do not target developers, bookkeeping files, hidden names, or other repositories. Content must include the existing frontmatter title and matching scope. The host applies the accepted Edits during artifact retention and regenerates the index and table of contents; return the edits without writing files. Additional extraction must use paths distinct from the first extraction.
