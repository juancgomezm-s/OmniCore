"""Exercise the shipped native WebP decoder through trusted stdio attachment upload, without inference."""
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import subprocess
import threading
import uuid


def smoke_webp(command, evidence, environment=None):
    evidence = Path(evidence).resolve()
    workspace = evidence / "workspace"
    workspace.mkdir(parents=True, exist_ok=False)
    data = evidence / "data"
    key = secrets.token_bytes(32)
    env = dict(os.environ if environment is None else environment,
               OMNICORE_CONFIG_DIR=str(evidence / "config"), OMNICORE_DATA_DIR=str(data),
               OMNICORE_TRUSTED_ACTION_BOOTSTRAP=key.hex().upper())
    encode = lambda value: json.dumps(value, separators=(",", ":"), ensure_ascii=True)
    canonical = str(workspace).replace("\\", "/").rstrip("/")
    if os.name == "nt":
        canonical = canonical.lower()
    workspace_hash = hashlib.sha256(canonical.encode("utf8")).hexdigest().upper()
    valid = base64.b64decode("UklGRh4AAABXRUJQVlA4TBEAAAAvAYAAAAdQslIUtP+BiOh/AAA=")
    # RIFF/chunk lengths and the lossless image header are intact, but pixels are absent.
    incomplete = b"RIFF" + (18).to_bytes(4, "little") + b"WEBPVP8L" + (5).to_bytes(4, "little") + valid[20:25] + b"\0"
    with (evidence / "host.stderr").open("wb") as diagnostics:
        host = subprocess.Popen([*command, "host", "--stdio"], cwd=workspace, env=env,
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=diagnostics)
        expired = threading.Event()

        def timeout():
            expired.set()
            try:
                host.kill()
            except ProcessLookupError:
                pass

        watchdog = threading.Timer(60, timeout)
        watchdog.daemon = True
        watchdog.start()
        try:
            def exchange(message_type, payload):
                identity = str(uuid.uuid4())
                envelope = dict(version="1.0", messageType=message_type, messageId=identity, payload=payload)
                host.stdin.write((encode(envelope) + "\n").encode("utf8"))
                host.stdin.flush()
                line = host.stdout.readline(1024 * 1024 + 2)
                if not line.endswith(b"\n") or len(line) > 1024 * 1024 + 1:
                    raise RuntimeError("WebP smoke: missing or oversized Host response; see host.stderr")
                response = json.loads(line)
                if response.get("messageId") != identity or response.get("messageType") == "error":
                    raise RuntimeError("WebP smoke: unexpected Host response")
                return response["payload"]

            hello = exchange("hello", {"supportedVersions": ["1.0"]})
            nonce = hello["trustedActionNonce"]

            def action(payload):
                identity = str(uuid.uuid4())
                payload_hash = hashlib.sha256(encode(payload).encode("utf8")).hexdigest().upper()
                signed = "\n".join(("omnicore-user-action-v1", nonce, identity, payload_hash, workspace_hash, "-", "-"))
                signature = hmac.new(key, signed.encode("utf8"), hashlib.sha256).hexdigest().upper()
                command_envelope = dict(version="1.0", messageType="command", messageId=identity, payload=payload)
                return exchange("command", {"operation": "command", "command": command_envelope,
                    "actionProof": {"nonce": nonce, "expectedSessionId": None, "expectedRunId": None,
                                    "signature": signature}})

            outcomes = []
            for name, content, expected in (("still.webp", valid, "Accepted"), ("incomplete.webp", incomplete, "Rejected")):
                upload = str(uuid.uuid4())
                for payload in (
                    dict(cmd="attachment.upload.begin", uploadId=upload, fileName=name, size=len(content),
                         sha256=hashlib.sha256(content).hexdigest()),
                    dict(cmd="attachment.upload.chunk", uploadId=upload, index=0, data=base64.b64encode(content).decode("ascii")),
                ):
                    result = action(payload)
                    if result["ack"]["outcome"]["kind"] != "Accepted":
                        raise RuntimeError("WebP smoke: upload admission failed")
                result = action(dict(cmd="attachment.upload.finish", uploadId=upload))
                if result["ack"]["outcome"]["kind"] != expected:
                    raise RuntimeError(f"WebP smoke: {name} did not produce {expected}")
                if expected == "Accepted":
                    reference = result["attachment"]
                    if reference["mediaType"] != "image/webp" or reference["size"] != len(content):
                        raise RuntimeError("WebP smoke: wrong attachment metadata")
                elif result["ack"].get("error") != "attachment_content_rejected":
                    raise RuntimeError("WebP smoke: incomplete pixels were rejected for an unrelated reason")
                outcomes.append({"image": name, "outcome": expected})
            host.stdin.close()
            if host.wait(timeout=10) != 0 or expired.is_set():
                raise RuntimeError("WebP smoke: Host did not exit cleanly")
        finally:
            watchdog.cancel()
            if host.poll() is None:
                host.kill()
                host.wait(timeout=10)
            if not host.stdin.closed:
                host.stdin.close()
            host.stdout.close()
    (evidence / "summary.json").write_text(json.dumps(outcomes, indent=2) + "\n", encoding="utf8")
    return outcomes
