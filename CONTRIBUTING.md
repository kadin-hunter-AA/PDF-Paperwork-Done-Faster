# Contributing

Thanks for helping. This project edits people's documents, so the bar is "never lose or silently change a user's file".

## Ground rules

- Read [AGENTS.md](AGENTS.md) and [design/WINDOWS11-DESIGN.md](design/WINDOWS11-DESIGN.md) before changing behavior or visuals. Use colors from [design/brand-tokens.json](design/brand-tokens.json).
- Keep originals intact. New output is create-only; failures and cancellation must leave the source unchanged.
- Every edit is one undoable checkpoint and must survive save/reopen and recovery.
- Expose only working features. If a case is not supported, refuse it with a plain explanation, or run it as a clearly labeled best-effort copy.
- Keep the distinctions: editing existing text vs adding text; visual signature vs digital signature; covering content vs permanent redaction.
- `development/fixtures/` is frozen. Add new fixtures by generating them inside a proof's output folder.
- Never commit real customer or personal documents. Use synthetic PDFs.

## Workflow

1. Build: `& ./development/tools/dotnet/dotnet.exe build ./development/HunterPdf.slnx --nologo` (0 warnings expected).
2. Add a proof for the feature in `development/App.Tests` (engine/session) and, for UI, `development/App.UiTests`. Each proof checks undo/redo, save/reopen, an independent reader (pypdf or MuPDF) and an unchanged source.
3. Run `App.Tests` and `development/tools/run-ui-checks.ps1`.
4. Record what passed and what is still open in `development/docs/BUILD-STATUS.md`.

## Reporting issues

Use the issue templates. Attach a synthetic PDF that reproduces the problem, never a real document.

## License

By contributing you agree that your contribution is licensed under the [AGPL-3.0](LICENSE).
