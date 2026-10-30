#!/bin/sh
# One integrated build, then real M9 contracts and Linux lifecycle on that output.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec sh "$root/scripts/validate-linux-m1-m8.sh" \
    -method 'OmniCore.Tests.LinuxBatchSandboxTests.*' \
    -method OmniCore.Tests.StdioTransportTests.Native_process_controls_cover_active_and_quiescent_run_then_passively_read_terminal_output \
    -method OmniCore.Tests.StdioTransportTests.Session_writer_lock_is_exclusive_between_processes_and_released_on_owner_death \
    -method OmniCore.Tests.StdioTransportTests.Unread_ack_reconnect_lookup_and_unknown_reservation_never_repeat_command_effects \
    -method OmniCore.Tests.StdioTransportTests.Real_stdio_stream_replays_then_delivers_live_while_queries_remain_responsive_before_ack \
    -method OmniCore.Tests.StdioTransportTests.Stdio_cli_usage_and_host_diagnostics_are_localized_for_all_four_locales \
    -method OmniCore.Tests.StdioTransportTests.Native_session_security_defaults_are_snapshotted_per_session_across_selection_and_host_reopen \
    -method OmniCore.Tests.StdioTransportTests.Native_human_shell_runs_through_host_without_provider_and_is_not_inherited_by_later_model_context \
    -method OmniCore.Tests.StdioTransportTests.Native_transcript_import_two_host_processes_share_receipt_and_replay_stably \
    -method OmniCore.Tests.StdioTransportTests.Native_model_selection_is_session_cas_scoped_and_catalog_does_not_expose_route_configuration \
    -method OmniCore.Tests.StdioTransportTests.Native_permission_grants_respect_run_scope_share_workspace_scope_and_session_ceiling_in_dispatch
