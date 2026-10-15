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

The generic wire command path does not grant `ITrustedUserActionClient` authority or establish a
human origin. This block does not implement push events, external human authorization, OmniCoder
TypeScript/Electron consumption, `media.read`, persistent process controls, or ConPTY. The process
transport is not itself a TUI connection; existing in-process and TUI routes are preserved.
