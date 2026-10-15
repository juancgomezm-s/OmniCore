"""Start the published TUI in a real Linux PTY, then exit by explicit keyboard input."""
import fcntl
import os
from pathlib import Path
import pty
import select
import signal
import struct
import sys
import termios
import time

executable = Path(sys.argv[1]).resolve(strict=True)
evidence = Path(sys.argv[2]).resolve()
evidence.mkdir(parents=True, exist_ok=False)
for columns, rows in ((120, 35), (80, 25)):
    root = evidence / f"{columns}x{rows}"
    root.mkdir()
    pid, fd = pty.fork()
    if pid == 0:
        os.chdir(root)
        env = dict(os.environ, TERM="xterm-256color", OMNI_LOCALE="en",
                   OMNICORE_DATA_DIR=str(root / "data"), OMNICORE_CONFIG_DIR=str(root / "config"))
        os.execve(str(executable), [str(executable), "tui"], env)
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))
    raw = bytearray()
    exited = False

    def read_until(deadline):
        global exited
        while time.monotonic() < deadline:
            if select.select([fd], [], [], .1)[0]:
                try:
                    data = os.read(fd, 65536)
                except OSError:
                    exited = True
                    return
                if not data:
                    exited = True
                    return
                raw.extend(data)
                if b"\x1b[6n" in data:
                    os.write(fd, b"\x1b[1;1R")

    reaped = False
    try:
        read_until(time.monotonic() + 8)
        if exited or b"Omni" not in raw or b"\x1b[" not in raw:
            raise RuntimeError("Published TUI did not draw its first screen")
        for _ in range(4):
            os.write(fd, b"\x1b")
            read_until(time.monotonic() + 1.5)
            if exited:
                break
        if not exited:
            raise RuntimeError("Published TUI did not exit after explicit Escape input")
        _, status = os.waitpid(pid, 0)
        reaped = True
        code = os.waitstatus_to_exitcode(status)
        if code != 0:
            raise RuntimeError(f"Published TUI exited with {code}")
        print(f"PTY {columns}x{rows}: real first screen, explicit Escape, exit 0")
    finally:
        if not reaped:
            os.killpg(pid, signal.SIGKILL)
            os.waitpid(pid, 0)
        (root / "terminal.ansi").write_bytes(raw)
        os.close(fd)
