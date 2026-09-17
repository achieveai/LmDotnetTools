# Control text tone research

## Current styles

- Composer Mode and Provider names use `#333` at weight 500. Provider's prefix uses `#666`.
- Project names inherit `#343a40` and use weight 600.
- Conversation titles use `#212529` at weight 500.
- These four labels carry more visual emphasis than the surrounding controls and metadata.

## Change

- Use one muted slate, `#5f6874`, and normal weight 400 for the named labels only.
- Keep menu options, active backgrounds, focus treatments, icons, disabled opacity, and transcript typography unchanged.
- Mode and Provider component styles affect their selector triggers; popup item classes remain separate.

## Contrast

WCAG 2.1 requires at least 4.5:1 contrast for normal text ([W3C contrast minimum](https://www.w3.org/WAI/WCAG21/Understanding/contrast-minimum)). Calculated sRGB contrast for `#5f6874`:

- `#f8f9fa` control background: 5.36:1
- `#e9ecef` hover background: 4.76:1
- `#e3e6e9` active sidebar background: 4.51:1
- white: 5.65:1

The selected tone meets 4.5:1 on every existing relevant background.

Final verification: local preview computed colors for all four requested labels are rgb(95, 104, 116), font-weight 400. Independent Sol screenshot review passed: secondary, legible, and distinct from disabled controls.
