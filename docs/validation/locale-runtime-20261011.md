# Runtime locale validation — 2026-10-11

## Scope

P1B runtime/CLI locale selection and the shared P6 localization paths touched by this block. Startup precedence is `--locale` > `OMNI_LOCALE` > a valid configured `ui.locale` > Spanish default. Locale selection is passed to the TUI through its optional constructor seam; preference preview/cancel synchronizes the CLI runtime without mutating process environment variables. `--locale` parsing preserves command arguments, quoting/token boundaries, and the `--` separator; a missing value exits safely before dispatch.

The settings file accepts only canonical `es`, `en`, `fr`, or `it` values, matching its schema. Regional values are normalized for process overrides and locale-save requests. Host locale errors use stable wire keys and localized resources rather than parsing exception prose.

## Validation status

Validated by the root integration batch after both implementation packages were ready. The final build completed with zero errors/warnings. Runtime, parser, configuration and resource cases passed in the broad batch and focused replay; final architecture passed 62/62. See the [combined evidence](preferences-batch-20261011.md) for exact overlapping batch counts, report paths and corrections. No full-suite or live-provider qualification is claimed.

## Added or updated coverage

- `LocaleRuntimeTests`: parser token preservation and missing-value diagnostics, locale-resource/key placeholder coverage, and canonical settings validation.
- `CliLocaleRuntimeIntegrationTests`: isolated precedence and runtime/plain localization wiring, preference preview/cancel callback synchronization, and safe exit for missing `--locale` before command dispatch.
- `UiLocalePreferencesTests`: configured-vs-default source evidence and stable localized Host error keys.

These cases were executed, including the Host wrapper correction that preserves the CLI assembly boundary and the configured/default distinction. The resource increment contains 35 shared keys in all four languages; it is not a complete migration of legacy copy.

## Known limits

- The legacy catalog is not fully translated for French and Italian. Paths without a runtime/shared translation can fall back to English or legacy Spanish; this block does not claim full four-language coverage of the roughly 538 legacy keys.
- Technical prompts, wire/JSON shapes, and tool identifiers remain untranslated/stable.
- Full legacy localization and localized command aliases remain separate planned work.
