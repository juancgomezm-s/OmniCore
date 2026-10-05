# TUI visual iteration from real driver frames

The visual fixture executes production `TuiApp.RunWith` on Terminal.Gui's DOTNET
driver with a real SQLite journal and synthetic durable assistant content. It
exports `IDriver.Contents` after the normal loop raises `LayoutAndDrawComplete`.
It does not capture Windows Terminal, automate its GUI, reconstruct ANSI, or
invoke a model. SVG glyphs use Consolas at a fixed cell pitch; host-terminal
font rendering, DPI and window chrome are not qualified by these artifacts.

## Reproduce

In a fresh PowerShell process, from the consolidation worktree:

```powershell
dotnet build tests/OmniCore.Tests --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
$env:TERM = 'xterm-256color'
$env:OMNICORE_TUI_SNAPSHOT_DIR = Join-Path $env:TEMP 'omnicore-driver-frames'
dotnet run --no-build --project tests/OmniCore.Tests -- -method '*Visual_frames_export_the_real_driver_cells'
```

Rasterize the five SVG files with Node and Sharp (installed dependency or an
explicit module path), without substituting any colors or content:

```text
node tools/render-tui-frames.cjs <snapshot-directory> [sharp-module-path]
```

Review the PNGs at 80x25, 100x30 and 140x40, edit the production renderer,
repeat the capture, then run all TuiWiringTests and ConversationPresentationTests.
Tests verify actual frame text, composer focus and bounded capture completion.
The existing wiring tests cover keyboard, resize, overlays, questionnaires and
polling-preserved history. Captures use only fixture data, not live user history.

## Iteration and evidence

- Theme fixes explicitly set foreground/background on root and child controls;
  default terminal colors no longer decide the normal colored appearance.
  NO_COLOR still honors terminal defaults.
- Header uses workspace name and available branch instead of a dominant full
  filesystem path. No journal or workspace-root semantics changed.
- Code rows pad to the real viewport width using display-column measurement;
  panels have uniform backgrounds. Viewport changes update presentation before
  polling; identical polls retain scroll position.
- First harness version forced redraw reentrantly and captured incomplete frames.
  Its failures were not counted as delivered screenshots. The completed-frame
  event and expected composer-frame readiness replace that approach.
- An intermediate production change reset history on the first poll; the existing
  history test caught it. The viewport-change hook corrected it without weakening
  the assertion. Final colored focal run: 15 total, 15 passed, zero skips/failures.
- First full run: 1348 total, 1343 passed, one failed, four skipped. Failure:
  SecurityP0Tests.Pinned_tls_handler_works_end_to_end_against_a_real_tls_server,
  TaskCanceledException during TLS read. All TUI cases passed. Original result:
  `%TEMP%/omnicore-tui-visual-full.xml`. Four skips are Windows symlink permissions.
- TLS isolated recheck passed (1/1). Second full run: 1348 total, 1342 passed,
  two failed, four skipped. The TLS timeout repeated; sidebar resize also timed
  out waiting for conversation width 120 after closing the sidebar. Its isolated
  colored recheck passed (1/1). Both default full runs are preserved and neither
  is reported green; the load-sensitive resize result remains an open diagnostic,
  not a confirmed production cause. Repeat result: `%TEMP%/omnicore-tui-visual-full-repeat.xml`.
- Architecture: 56/56 passed. Local visual artifacts were reviewed at all three
  dimensions. Renderer snapshots do not qualify host terminal font/DPI behavior.

No M4 milestone closure, provider qualification, desktop screenshot, full Markdown
support or graphical copy button is claimed by this iteration.

## Borderless contrast iteration

- Composer, sidebar and overlays use flat surfaces, without line borders. Help
  and command shortcuts sit below the composer. Code fences use shaded rows,
  not box-drawing characters. Buttons have no decorations or shadow.
- Composer background is slate #293648; panels/menus #202C3B; primary control
  text #F4F7FB and help #B9CBDF. Focused buttons use #3D5068 with white text.
  Conversation stays #061822; NO_COLOR remains supported.
- Five captures cover conversation at three sizes, an open sidebar and a notice.
  Reviewed actual driver cells; this is not a host desktop capture.
- First tonal full run: 1350 total, 1345 passed, one failed, four skipped.
  The visual fixture timed out at its resize wait; TLS passed this run.
  Preserved result: `%TEMP%/omnicore-tui-tonal-full.xml`.
- Fixture resize now uses IDriver.SetScreenSize, keeping exact size assertions
  and bounded waits. Subsequent focal runs passed 17/17, including the revised
  contrast palette. No global parallelism or assertions were weakened.
- Latest contrast full run: 1350 total, 1346 passed, zero failed, four Windows
  symlink-permission skips. Preserved result:
  `%TEMP%/omnicore-tui-tonal-contrast-full.xml`. This successful run does not
  erase the preceding load-sensitive failures or establish their root cause.

## Hierarchy and spacing iteration

- Header, workspace title, message title and prompt use #85E6DF. Overlay titles
  use a #344559 band on the borderless menu surface.
- Composer is three rows tall. Autocomplete is a root-level row below the
  composer, followed by permanent keyboard help; it no longer occupies the
  message surface. Wiring verifies parent and vertical position of suggestions.
- Reviewed updated narrow conversation and open notice frames. First focal
  result: 16/17 passed; sidebar capture subscribed before the requested sidebar
  frame was drawn. Capture readiness now requires the scene's Workspace label
  as well as input text, with the original timeout and assertions retained.
  Repeat: 17/17 passed. XMLs: `%TEMP%/omnicore-hierarchy-focal.xml` and
  `%TEMP%/omnicore-hierarchy-focal-repeat.xml`.
- Full hierarchy run: 1350 total, 1345 passed, one TLS timeout failed, four
  symlink-permission skips. All TUI tests passed. Preserved result:
  `%TEMP%/omnicore-hierarchy-full.xml`. Existing TLS load-sensitive diagnostic
  remains open; the preceding green full run is not reused as this run's result.

## OmniCoder-style conversation pipeline

- Inspected OmniCoder's WPF StyledTextPresenter, MarkdownRenderer and
  SyntaxHighlighter. Adapted the syntax scanner and its 17 language definitions
  into internal CLI components, without a dependency on OmniCoder or WPF.
- Pipeline: ConversationPresentation adds role labels and preserves non-assistant
  text literally; MarkdownRenderer recognizes fences and the existing inline
  subset; SyntaxHighlighter produces semantic spans; StyledTextPresenter paints
  real TextView cells with full-width block backgrounds.
- Terminal Markdown keeps the stricter matching fence character/length behavior,
  original indentation, empty rows and unclosed streamed blocks. It does not import
  OmniCoder's complete Markdown subset, WPF links, emoji rasterization, context
  menus or graphical copy button. Syntax is a bounded lexical heuristic, not a
  compiler: >200000-character code and unknown languages remain uncolored;
  >400000-character Markdown stays literal.
- Keywords, types, functions, strings, numbers, comments and punctuation now have
  distinct colors. NO_COLOR keeps terminal defaults. Identical polling still
  avoids reloading the document.
- Selection text and the Copy command use only selected graphemes. Wiring tests
  replace the driver's clipboard with FakeClipboard, not the user's clipboard,
  and verify both the copy result and selection across two production polls.
- First focal runs caught a changed per-span test contract (prefix now separate)
  and an ambiguous culture-based ESC assertion. Tests retain exact reconstructed
  text/style checks and now check the ESC character literally. Those XMLs remain
  in TEMP; neither failure was counted green or treated as a framework root cause.
- Final: build zero warnings/errors; colored focal 59/59 passed; NO_COLOR wiring
  14/14 passed; full 1392 total, 1388 passed, zero failed, four Windows symlink
  permission skips. XMLs: `%TEMP%/omnicore-syntax-focal-final.xml`,
  `%TEMP%/omnicore-syntax-no-color-final.xml`, `%TEMP%/omnicore-syntax-full.xml`.
- Updated syntax-frames reviewed at 100x30 and 140x40. These are actual renderer
  frames, not host screenshots. No M4 closure, journal changes or provider calls.
