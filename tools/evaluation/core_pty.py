"""Windows acceptance driver; pywinpty is supplied explicitly by the test operator."""
import queue
import re
import threading
import time

def answer_for_prompt(text):
    if 'Choose an option (default: deny):' not in text:return None
    has_once=re.search(r'\[\d+\]\s+Allow once(?:\s|$)',text) is not None
    if has_once and ('Strong sandbox unavailable' in text or 'paths outside the workspace' in text):
        return 'consent_once'
    if has_once and 'Permission required' in text:return 'allow_once'
    return 'deny'

def execute(argv, cwd, env, output, timeout=240, interrupt_when=None):
    from winpty import PtyProcess
    process=PtyProcess.spawn(argv,cwd=str(cwd),env=env,dimensions=(40,180))
    chunks=queue.Queue()
    def reader():
        try:
            while True: chunks.put(process.read(65536))
        except (EOFError,OSError): chunks.put(None)
    threading.Thread(target=reader,daemon=True).start()
    start=time.monotonic(); pending=''; decisions=[]; interrupted=False
    try:
        with open(output,'w',encoding='utf-8') as log:
            while process.isalive() or not chunks.empty():
                if time.monotonic()-start>timeout:
                    process.terminate(force=True); decisions.append('deadline'); break
                if interrupt_when and interrupt_when():
                    process.terminate(force=True); interrupted=True; break
                try: chunk=chunks.get(timeout=.1)
                except queue.Empty: continue
                if chunk is None: break
                log.write(chunk); log.flush(); pending=(pending+chunk)[-16000:]
                clean=re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]','',pending)
                answer=answer_for_prompt(clean)
                if answer is not None:
                    # Explicit harness authorization is limited to disposable fixtures.
                    # Never increase budget, promote memory, switch routes or answer Ask
                    # outside the displayed permission/WeakSandboxConsent choices.
                    process.write(answer+'\r\n'); decisions.append(answer); pending=''
            if process.isalive(): process.terminate(force=True)
            exit_code=process.exitstatus
    finally:
        process.close(force=True)
    return {'exitCode':exit_code,'elapsedSeconds':time.monotonic()-start,'decisions':decisions,'interrupted':interrupted}
