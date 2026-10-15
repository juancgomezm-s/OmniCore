# OmniCore stdio protocol 1.0

This directory contains the versioned CLI-to-Host stdio contract. `goldens.jsonl` contains complete
wire envelopes consumed by the protocol tests. Durable event schemas and upcasters remain separate
from the wire `1.0` version (ADR-0013).

The child is started as `omni host --stdio`. The client first sends `hello` with `supportedVersions`;
the Host selects `1.0` only when offered, returns the capability list, and correlates the response
with the request `messageId`. Command and query requests retain the M9-A operations. Legacy `subscribe`
still returns the complete replay or an error. The separately advertised `events.replay.page`
operation takes an explicit Session, bounded row count, cursor and optional pinned high-water mark;
the response reports `nextSequence`, `highWater`, `complete` and `hasGap`. An empty Host returns an
empty complete page. The Client uses the legacy operation only when a Host does not advertise paged
replay; explicit page or receipt lookup against an unsupported Host fails before sending a request.

Frames are strict UTF-8 JSON Lines, one object per physical line, with a 1,048,576-byte limit measured
after encoding. A writer rejects literal CR/LF before writing bytes; escaped newlines in JSON strings
remain valid. The replay Host fixes the first-page high-water mark for the entire batch and reduces
the SQL prefix until the final envelope fits the frame. One event that cannot fit returns
`frame_too_large`; it never emits a partial frame. Gap flags cover purged history and missing
sequence values. stdout contains protocol frames only, diagnostics use stderr, and EOF between frames
closes normally. There is one request in flight per connection.

The Client negotiates with a 10-second timeout by default; callers can pass a finite timeout and
cancellation token. Timeout/cancellation terminates only the child it started and returns no synthetic
response. Capabilities are retained and checked before use.

For persistent SQLite workspaces, a permanent per-Session lock path is held with exclusive OS file
sharing. The operating system releases it after process death; the lock path is never removed or
recreated. Writers acquire ownership before mutation, and reader-to-writer transitions refresh the
session pointer and projection. Read-only queries remain available when a Session is owned elsewhere.
This mechanism was tested on Windows local storage only; network filesystems, Linux and macOS have not
been verified.

`command.result.lookup` reads a durable receipt keyed by command ID and request hash; it never resends.
Completed receipts retain a Session binding and safe acknowledgement fields, not input content.
Changing content under an existing ID is rejected. A reservation left by interruption returns
`unknown` and cannot be replayed automatically. Lookup plus paged journal replay allows a caller to
reconcile completed commands across a new Host process, but ambiguous commands stay ambiguous; this
is not a universal exactly-once guarantee.

## M9-D optional trusted-action extension

The extension is additive to stdio 1.0. A Host without the private bootstrap continues to advertise
the M9-A/B capabilities unchanged and does not advertise `trustedActionNonce`, `query.humanActionScope`,
or any `.user` capability. A persistent SQLite Host started by the trusted Electron main may additionally
advertise `command.session.input.user`, `command.run.mode.select.user`, `command.run.interrupt.user`,
`command.run.cancel.user`, and `query.humanActionScope`. These names describe supported operations; they
do not themselves grant consent or authority.

After `hello`, the trusted Host returns a fresh per-connection `trustedActionNonce`. The main process
queries `humanActionScope` and obtains the physical workspace hash from its own launch configuration.
For a trusted command, the outer `command` request may include this optional object:

```json
{"operation":"command","command":{"version":"1.0","messageType":"command","messageId":"<command-id>","payload":{"cmd":"session.input","text":"...","mode":"plan","expectedRevision":3}},"actionProof":{"nonce":"<hello nonce>","expectedSessionId":"<canonical-guid-or-null>","expectedRunId":"<canonical-guid-or-null>","signature":"<uppercase hex HMAC-SHA-256>"}}
```

The HMAC key is 32 random bytes supplied to the CLI only through its private process environment;
the CLI consumes and removes that variable before runtime composition, retains the key in memory only,
and clears the byte buffer on exit. It is never sent to the renderer, included in argv, journal, receipt,
or logs. The signed UTF-8 message is the following newline-separated ASCII/hex fields:

```text
omnicore-user-action-v1
<trustedActionNonce>
<commandId>
<uppercase hex SHA-256 of exact command.payload JSON UTF-8 bytes>
<uppercase hex SHA-256 of canonical physical workspace UTF-8 bytes>
<expectedSessionId or ->
<expectedRunId or ->
```

The Host nonce is 16 random bytes rendered as uppercase hex. The workspace string used for its hash is
`ResolvePhysicalWorkspaceRoot(Environment.CurrentDirectory)` followed by `CanonicalWorkspacePath`: full
path, `/` separators, trailing `/` removed, and lowercase invariant on Windows only. Session/Run proof
values are canonical GUID `D` format strings or JSON `null`; `null` contributes a single `-` line. A
non-null Session/Run is independently checked against the active Host scope.

The Host verifies HMAC with constant-time comparison and checks the connection nonce, current physical
workspace, command shape and scope. It revalidates active Session/Run and revision under Session writer
ownership before dispatch. `session.input.user` accepts only `mode:"plan"`, non-empty text up to 32,000
characters and an `expectedRevision` (nullable only when no Run exists); this operation records input and
opens/updates a Run but does not invoke inference. `run.mode.select.user` requires `runId`, `mode` (`plan`
or `act`) and exact non-negative `expectedRevision`. `run.interrupt.user` and `run.cancel.user` require
the exact active `runId`. Proofs for other command names or extra payload fields are rejected. Generic
`session.input` retains its prior behavior and never acquires trusted authority. `run.interrupt`,
`run.cancel` and `run.mode.select` through the generic command path return Rejected.

Only the native confirmation in Electron main establishes the grant for a workspace. The proof is
scoped to the connection nonce, command, payload and Session/Run; reconnecting revokes it. The trust
boundary is the same local operating-system principal: this mechanism separates renderer/model and
generic-wire authority but does not claim to defend against a malicious process running as the same user.
Durable receipt lookup is still no-resend; it is not a universal exactly-once guarantee.

Not implemented by this extension: Core chat/inference responses, generic external human clients,
push events, `media.read`, persistent process controls, or ConPTY. Existing in-process and TUI routes
are preserved. Goldens remain the legacy stdio 1.0 envelopes; the optional proof does not alter the
durable event schema or wire version.

OmniCoder M9-C/D consumes this contract from Electron main without loading CLR. Its supervised Host
and Sidecar provide connection health, queries, paged replay, explicit Plan input, mode selection,
interrupt and cancel. Renderer cannot access the process or signing key; Electron main displays the
native action confirmation. Input does not produce a model response. See [M9-C](../../../validation/m9-block-c-20261014.md)
and [M9-D](../../../validation/m9-block-d-20261014.md) for exact product scope and evidence. Neither
block introduces a new wire version.

## M9-E conversation execution extension

This additive extension keeps wire version `1.0` and is advertised only when the trusted conversation
runner is available. It adds `command.conversation.start.user`; its command payload is
`{"cmd":"conversation.start","text":"...","mode":"plan|act","expectedRevision":null|N}` and
uses the M9-D `actionProof` over the exact payload. The command writes one human input and admits the
existing persisted Run. It then runs the normal `OmniCliRuntime` conversation pipeline for that exact
Session, Run and admitted command; it does not resubmit `session.input` or alter the User default mode.
The generic `command.session.input` remains unchanged and does not invoke inference.

An `Accepted` command acknowledgement means only that input/Run admission was durable; it is not a
model completion. `conversation.result` with `commandId` and the SHA-256 `requestHash` reads the durable
execution receipt without resending. It reports `running`, `completed`, `failed`, `deferred`,
`cancelled`, `unknown`, `not_admitted`, `not_found` or `id_conflict`, with the bound `sessionId` and
`runId` when available. `code` is a stable safe classification (`turn_completed`, `turn_cancelled`,
`input_required`, `model_tools_unsupported` or `runtime_failed`); it is not the raw provider diagnostic.
Canonical assistant content and terminal Run events remain in the journal and are recovered by the
existing bounded replay operation. The consumer drains replay before presenting a settled turn, and
deduplicates by event identity. No token stream, TTFT, usage or cost is invented from final text or
replay. Consent/policy failures stay deferred or failed, never become assistant messages.

This is request/replay polling, not push streaming. It does not add exactly-once provider execution:
after an ambiguous execution receipt the Client performs lookup and replay only, never a blind resend.
Only a real terminal journal event establishes the Run outcome; an admission receipt is transport
metadata, not a second source of Run state. The current Electron consumer is wired to the existing
composer and preserves Pi as the per-workspace legacy backend. It exposes Core's effective model but
does not route Core selection through Pi's provider selector.

The earlier goldens continue to pin legacy wire envelopes; this extension adds capability and
conversation behavior without changing those envelopes, durable event schemas or protocol version.
See [ADR-0051](../../../adr/0051-conversation-inference-from-persisted-input.md) and the
[M9-E validation record](../../../validation/m9-block-e-20261014.md).

## M9-F attachment intake extension

Wire `1.0` keeps its frame limit at 1 MiB. The optional `attachmentUpload` capability is advertised
only by a Host that has the bounded upload path. Electron main stages an immutable file snapshot as
`attachment.upload.begin.user`, one or more ordered `attachment.upload.chunk.user` requests (each
chunk is at most 512 KiB of decoded bytes), and `attachment.upload.finish.user`. Begin binds an
upload ID to a safe file name, exact byte count, and SHA-256. Finish verifies the complete byte
count/hash and content-derived media type before placing the snapshot in CAS. The resulting
`ArtifactRef` is returned by Host and included in the exact `conversation.start.user` payload signed
by the existing M9-D proof; the proof therefore covers the attachment references and manifest.
Incomplete uploads are connection-local, bounded, expire after three minutes, and are removed when
the Host connection closes. The request frame remains UTF-8 JSON; Base64 is used only for each
bounded binary chunk, never for a whole file or as model-visible text.

The Host accepts up to eight attachment references per input and 16 MiB per file. Supported
document intake is strict UTF-8 text/plain, Markdown, CSV, JSON, PDF and DOCX. PDF text extraction
uses PdfPig with a 100-page/200,000-character bound; DOCX extraction reads bounded OOXML entries,
rejects DTDs and caps expanded data. Extracted text is stored as a separate sensitive CAS snapshot
with an extractor identity linked from the immutable original, so replay does not reopen or
re-extract the source. Scanned PDFs remain documents with no extracted text; OCR is not performed.
Image MIME is identified from bytes for PNG/JPEG/WebP and constrained by byte/pixel/dimension
limits. Intake validates signatures and image headers/dimensions, not a complete decode of every
compressed image stream; malformed streams may be rejected by the provider. WebP VP8L is currently
unsupported (VP8/VP8X layouts only).

The Host verifies that each reference belongs to the input and its CAS bytes/hash are intact. The
effective model profile and provider dialect must support the requested input modality before the
Host admits a conversation command. `media.read` reads only a path admitted by normal filesystem
authorization/resource claims, snapshots verified bytes to CAS, and returns typed image content;
a CAS reference by itself grants no path access. Image input does not imply native tool calling.
Provider serialization is capability/dialect gated and reports unsupported combinations explicitly.
Audio and video are outside this block and are not advertised.

This extension adds no durable journal schema or wire-version change. The generic `session.input`
contract and in-process CLI route are preserved. See [ADR-0052](../../../adr/0052-multimodal-intake-and-materialization.md)
and the [M9-F validation record](../../../validation/m9-block-f-20261015.md) for implementation scope,
provider/UI wiring, platform coverage, and remaining limits.

## M9-H durable event stream extension

The optional `events.stream.v1` capability keeps wire version `1.0` and legacy clients unchanged.
`events.stream` binds a new `streamId` to the requested `sessionId`, durable `fromSequence`, and a
bounded credit window (1–32 frames). The Host responds with the accepted stream/cursor/window;
`events.ready` starts delivery. `events.ack` returns credit through an event sequence only after the
client consumer finishes applying it; `events.stop` cancels the subscription and waits for its
bounded journal pump to stop. Event frames carry `streamId`, `sessionId`, sequence, and the canonical
mapped `WireEvent`. Responses still carry their request `messageId`; stream events carry no request
correlation ID and are demultiplexed independently by the single stdout reader.

The Host reads durable journal pages only while credit is available. If a page contains only
internal events excluded by `ProtocolMapper`, the cursor advances to `page.nextSequence` even when
the page emitted no event frame. Exhausted credit pauses journal reads without blocking commands or
mutations; returned credit resumes from the acknowledged durable cursor. The journal remains the
source of truth and transport memory remains bounded. Frame UTF-8 size is bounded by the existing
1 MiB stdio limit. Invalid, future, duplicate, wrong-stream, wrong-Session, or wrong-connection ACKs
cannot enlarge the window or skip durable history.

If retained history no longer covers the cursor, the Host sends a gap control frame with requested
cursor, next available sequence, and high-water. The client reconciles the suffix through bounded
replay and retains the gap diagnostic; it must not report complete history. Stop/rebind uses a new
stream identity, and a previous pump must finish before stop succeeds. EOF/cancel discards volatile
state; after a connection is restored, a client may create a new subscription from the durable
Session/cursor. Automatic transport reconnection is not provided. The .NET `omni stdio watch`
command requires the capability and reports its absence; it does not fall back automatically. The
TypeScript supervisor uses the existing paged replay path only when the capability is absent.
Neither path resends an ambiguous command or reissues human proof. Clients without the capability do
not claim push delivery.

This carries canonical durable events only. Provider token streaming, TTFT, usage/cost synthesis,
and changes to proof authority are outside scope. See [ADR-0054](../../../adr/0054-event-stream-backpressure-stdio.md)
and [M9-H validation](../../../validation/m9-block-h-20261015.md).
