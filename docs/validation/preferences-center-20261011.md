# Preferences center validation — 2026-10-11

## Scope delivered

- Replaced the four-button preferences overlay with a searchable center backed by the nine `PreferenceCategoryCatalog` categories, with wide and narrow keyboard layouts.
- Added localized center resources and search terms for Spanish, English, French, and Italian.
- Connected existing account/API, model-policy, tools, sidebar, and animation actions. Context and permissions use readable scrollable inspection views where available; unsupported capabilities are described without presenting fake actions.
- Added General locale preview/save/cancel/reload behavior with revision checks, source/override visibility, callback synchronization, and preserved TUI conversation state.
- Added local-engine list/add/edit UI through `ITuiLocalEngineHost`; this reads and saves configuration only and does not probe, start, or stop engines.
- Added focused resource, layout, locale workflow, override, navigation, state-preservation, local-engine, and argument round-trip coverage. Added terminal-frame cases for the preferences center.

## Validation status

The owner requested broad implementation packages followed by coordinated validation. Root built the combined delivery, ran one broad focused batch, and replayed only affected groups after corrections. The final UI/preferences/frame selection passed 59/59; architecture passed 62/62 and the final build had zero errors/warnings. These overlapping counts must not be added to the earlier batches. See the [combined evidence](preferences-batch-20261011.md) for the initial failures, fixes, report paths and exact validation scope.

Actual Terminal.Gui driver frames were exported and visually inspected for the wide center, compact French/Italian General, process locale override and local editor. These are isolated fixture frames, not desktop captures. The inspection prompted complete notice/help heights and explicit argv guidance; navigation tests detected and verified fixes for Esc routing and child focus. New center resources contain 119 keys translated into all four languages.

## Known boundaries

- Local engines can be listed, added, and edited; deletion is not exposed by the current host façade.
- Saving a local-engine configuration requires reopening OmniCore to use it; it does not claim to reload a running engine.
- This center does not implement new OAuth flows or a local inference-engine runtime/editor. Existing connection and model screens remain the integration points for capabilities already present.
- Locale coverage added here is for new center resources. It does not imply complete French or Italian coverage across older application surfaces.
- The tools/context/permissions pages expose current available inspection contracts; they do not claim that every planned preference capability or future contract is implemented.
- Wide/compact layouts are selected on opening. Reflow when crossing the width threshold while already open and full NO_COLOR/ASCII accessibility coverage remain pending; P2 is not closed in full.
