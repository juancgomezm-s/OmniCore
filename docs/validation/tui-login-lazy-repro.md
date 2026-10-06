# TUI login TextView initialization diagnostic

## Purpose

`TuiWiringTests.Repeated_login_open_cancel_reinitializes_exact_progress_textview_on_ui_loop`
exercises 20 sequential login-panel openings on the real Terminal.Gui driver. Each cycle
creates the production login `TextView` with its exact initial text, `ReadOnly` and
`WordWrap` settings, starts a pending fake login, cancels with Esc, waits for cancellation
and overlay closure, and drains the late progress callback before the next cycle.

The account host is a test fake; there are no provider calls or account credentials.
The assertions are intended to remain strict and the test contains no retry loop.

The fixture initializes Terminal.Gui on the same `tui-wiring-loop` thread that runs
`TuiApp.RunWith`. Its `Invoke` helper executes inline only on that recorded UI thread;
otherwise it waits for the queued action and propagates its exception. The retained
`Driver_main_thread_identity_matches_the_thread_running_the_ui_loop` assertion checks this
thread affinity.

## Validation status

Before correction, the affinity regression failed: UI loop thread 11 versus recorded
Application.MainThreadId 6 (`tui-thread-affinity-before-test.log`). After correction,
all 59 TuiWiring tests passed in 86.261 s (`tui-affinity-wiring-tests.log`), including
20 login cycles and live resizing. The full suite passed 1692 of 1696 cases, with zero
failures and four symlink-permission skips in 201.818 s
(`routeid-router-affinity-full-suite.log`). These overlapping fixture results do not
establish authenticated usage or prove every intermittent framework failure resolved.

The concurrent animation color assertion also waits for a completed driver frame containing
the `Procesando` activity marker before inspecting child schemes. Color expectations remain
assertions, not frame-readiness criteria.

## QA frame dimensions

The conversation-cell QA fixture now sets its requested driver dimensions after
`Application.Init` and before starting the event loop, so the tall QA render begins at its
target size. This is a test setup adjustment, not a claim that the framework resize failure
is fixed or understood. Live resize coverage remains in
`Sidebar_and_conversation_respond_to_f2_and_real_screen_resizes`.
