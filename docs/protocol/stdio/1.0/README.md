# OmniCore stdio protocol 1.0

This directory contains the first implemented CLI-to-Host stdio contract. The machine-readable
fixtures in `goldens.jsonl` are newline-delimited complete wire envelopes; the tests load this same
file from `docs/` and check its framing and negotiated capability list.

The child is started as `omni host --stdio`. The client must first send a `hello` envelope with
`supportedVersions`. The Host selects `1.0` only when offered, returns the exact capability list,
and correlates the response with the request `messageId`. Later requests are `command` envelopes
whose payload operation is `command`, `query`, or `subscribe`. Command payloads carry an embedded
wire command; query payloads name one allowlisted read model; subscriptions use `fromSequence` and
return the journal replay as one bounded batch. Every response uses the outer request id.

Frames are one UTF-8 JSON object per physical line, no BOM, strict UTF-8, at most 1,048,576 bytes
before decoding. A writer rejects literal CR/LF before writing any bytes; JSON-escaped line breaks
inside strings are permitted. Empty/truncated frames and invalid UTF-8/JSON are connection errors.
stdout carries wire frames only; diagnostics are written to stderr. EOF between frames closes
normally. There is one request in flight per process connection. The process client bounds hello
negotiation to 10 seconds by default; callers may provide a positive finite timeout and cancellation
token. Timeout/cancellation terminates the owned child process and yields no synthetic response.

Responses and complete replay batches use the same 1 MiB ceiling. If a query or replay batch is
too large, the Host returns `unsupported_request` and sends no partial result. Version 1.0 has no
`nextCursor` or `toSequence` paging fields, so an oversized replay cannot be paged through this wire.

Capabilities describe implemented request shapes, not guaranteed outcomes. In particular,
`command.session.input` does not authenticate a human or grant trusted-user authority. Commands
that need `ITrustedUserActionClient` authority continue to be rejected. A malformed operation is
answered with `unsupported_request`, and the connection remains available for later requests. This contract does not
provide push events, leases, reconnection, deduplication, ambiguous-result resolution, or
exactly-once effects. Replaying from sequence N is a journal read, not a reconnection guarantee.
Only the CLI adapter is included; OmniCoder's TypeScript consumer remains future work.
