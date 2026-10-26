# Windows verifier startup: personal investigation and local correction

Status: local correction of two reproduced startup defects, prepared by the primary agent without workers. The original Windows CI hang still needs exact-candidate CI confirmation after Dot's personal approval. No push, PR update, merge or remote publication is authorized by these local results. M9 and M8 remain open.

## Starting evidence and controls

The investigation starts at local commit `5068687e86c31cf7066e2fa2b21b0dd2f7dddcfe`. The original failure belongs to published `72def746635fb9584e428fc4dc2bed9638fb27f9`, CI run `40396357036`, Windows job `121830321089`: the natural workflow row stopped after nine provider requests without an execution receipt, then fixture snapshotting encountered a busy CAS publication lease. That log does not identify the launch phase or lease owner.

The failing CI used SDK `10.0.403` and runtime `10.0.14`; the installed local versions were `10.0.401`/`10.0.12`. A private SDK copy matching CI was downloaded from Microsoft's release metadata, verified against its SHA-512, and kept under the ignored diagnostic directory. No installed SDK or global SDK configuration was replaced. Package provenance is recorded in `artifacts/m9-windows-ci-cause-20261026/ci-sdk-download.json`; the official [release notes and downloads](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md) identify the matching SDK/runtime pair.

Before changing production code, real AppContainer launches of a private native command and `dotnet --version` completed. The existing workflow also completed using a private SDK with actual `Available` preflight, Strong start/wait and exit 0. A new order-control fact reproduced CI's Weak-first, natural-second ordering under the exact CI runtime and passed in 123.297 seconds. These controls do not reproduce the original CI failure and do not prove that ordering or the SDK patch caused it.

## Reproduced defects and changes

`HasAppContainerReadExecute` read explicit ACL rules only. The installed SDK's existing ALL APPLICATION PACKAGES read/execute entries are inherited, so the launcher failed to recognize effective access. It attempted an unnecessary inheritable profile grant across the SDK tree; this can require changing permissions on a protected installation and waiting on the shared SDK ACL mutex.

The new real-Strong regression initially failed its ACL comparison: the command had completed with exit 0, but the SDK directory acquired a new profile ACE despite already-readable inherited permissions. Its original source, test and Sandbox DLLs, and red output are retained separately in `inherited-acl-red-image/` and `inherited-acl-before.*` under the diagnostic directory. The correction includes inherited effective entries and excludes inherit-only entries. Existing denial checks and the LPAC opt-out remain in force. The grant/rollback machinery is unchanged for paths that genuinely require profile access.

The second reproduced defect is the unconditional `Mutex.WaitOne()` in ACL grant acquisition. A test held the SDK's actual named mutex, requested cancellation of a real Strong launch and kept the competing mutex held. Before correction the launch did not observe cancellation: the assertion expected `OperationCanceledException` but timed out after two seconds. The test then released its owned mutex and awaited cleanup; no foreign process or lock was affected. Its red source/images are preserved in `acl-lock-red-image/` and `acl-lock-before.*`.

The launch token now reaches ACL grant acquisition, which polls the mutex with cancellation checks and releases it only when acquired. An abandoned mutex still transfers ownership correctly. ACL restoration deliberately does not inherit the canceled token, so cancellation cannot abandon grants. The regression also performs a second real Strong launch in the same workspace to prove the profile semaphore and earlier grants were released. No sandbox downgrade, new permission, tool, model request or executable substitution is introduced in product behavior.

These defects explain reproducible startup stalls and unnecessary SDK ACL work locally. The earlier CI log lacks the boundary evidence needed to assert which operation caused its original two-minute wait. A local pass must not be presented as proof of the remote cause.

## Validation scope

Final build logs and direct xUnit outputs are under `artifacts/m9-windows-ci-cause-20261026/`. The focused final campaign runs, sequentially on the final image with CI's SDK/runtime:

- cancellation while the SDK ACL mutex remains held, followed by a real launch on the same profile;
- preservation of inherited-readable SDK ACLs while a real Strong process is owned;
- readable workspace versus denied outside file;
- denied network access;
- LPAC read-only extension with denied checkout-wide writes;
- timeout termination of the complete AppContainer job tree;
- the full native workflow with forced Weak followed by natural Strong, real process receipts, integration artifacts and cleanup.

The final result is **7/7 facts passed**, zero failures/skips/not-run. The last fact completed in 118.369 seconds and executes two complete workflows; it is counted as one xUnit fact, not two cases or a rerun of the predecessor's twelve OS3 cases. `final-focal-results.json` records actual outcomes and per-run PIDs; all owned process handles returned exit 0. Final Test/Host dependencies and Sandbox net10 build completed without warnings/errors, as did Sandbox `net8.0` compatibility. No OmniCoder checkout was built or modified. All runner homes, configuration and provider credentials were isolated; scripted local HTTP responses are fixture inputs, not live inference.

The standalone diagnostic initially picked an archived Sandbox DLL through MSBuild's candidate-assembly search. Those intermediate probe failures and failed probe builds are retained as development evidence and excluded from acceptance. Its corrected project disables default items and resolves the explicit DLL hint before raw framework paths; hashes are checked before running it. `installed-sdk-verified-after.log` demonstrates actual Strong execution of the installed SDK, with its directory ACL unchanged while the process is owned. That probe predates the subsequent cancellation-only increment; final acceptance is attached to the exact final campaign image.

## Remaining evidence gate

The complete Windows CI job and its original Windows Server environment have not been rerun on this correction. The retained local traces, red reproductions, source/binary manifests and final outputs are for Dot's personal review before any publication. Synchronous fixture metadata collection and cancellation cleanup are not claimed to be bounded in every failure mode. The correction preserves mandatory ACL revocation, which can still wait for a competing ACL writer during cleanup. No global M9 closure, visual acceptance, Linux execution or remote CI success is claimed.
