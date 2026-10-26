# Design deliverables

Start with [WINDOWS11-DESIGN.md](WINDOWS11-DESIGN.md). The user-supplied Hunter OPS specification is authoritative. [brand-tokens.json](brand-tokens.json) contains exact values for implementation.

Five product icon studies are in `logos/`: classic, geometric, soft rounded, outline, compact. These retain the page and purple quill with gold nib, removing the crown, document lines and background tile. `logos/PROMPTS.json` records the built-in image generation prompts; `logos/reference.png` preserves the supplied reference.

Concept artwork requires selection and final vector reconstruction with exact flat colors and optical corrections at small sizes before ICO/installer integration. Generated pixels may contain unintended shading despite flat-color prompts; the tokens and specification take precedence.

The GUI specification covers Home, document tabs, View/Edit/Markup/Pages modes, contextual properties, page navigation, combining, safe saving and future PDF tools. The running WPF application has not been modified. Local `AGENTS.md` instructs future work to consult this design.
