"""Single-fault loopback relay to one fixed subscription endpoint; no request logging."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import http.client
import json
import threading

@contextmanager
def one_provider_failure():
    facts={'injected503':0,'forwardedResponses':0,'upstream':'https://chatgpt.com/backend-api/codex/responses'}
    lock=threading.Lock()
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*_):pass
        def do_GET(self):self.send_error(426,'Evaluation relay requires SSE')
        def do_POST(self):
            if self.path not in ('/codex/responses','/backend-api/codex/responses'):
                self.send_error(404);return
            size=int(self.headers.get('Content-Length','0'))
            if not 0<size<=2*1024*1024:self.send_error(413);return
            body=self.rfile.read(size)
            # Native Pi can compress its JSON request. Relay bytes unchanged; the
            # selected model is checked in the independent runtime receipts instead.
            with lock:
                inject=facts['injected503']==0
                if inject:facts['injected503']+=1
            if inject:
                error=b'{"error":{"type":"server_error","message":"Intentional one-shot evaluation fault"}}'
                self.send_response(503);self.send_header('Content-Type','application/json');self.send_header('Retry-After','0')
                self.send_header('Content-Length',str(len(error)));self.end_headers();self.wfile.write(error);return
            # Credentials remain in memory and are forwarded only to their original,
            # fixed HTTPS destination. Never persist headers, bodies or OAuth material.
            connection=http.client.HTTPSConnection('chatgpt.com',timeout=120)
            headers={k:v for k,v in self.headers.items() if k.lower() not in ('host','connection','content-length','accept-encoding')}
            headers['Accept-Encoding']='identity'
            try:
                connection.request('POST','/backend-api/codex/responses',body=body,headers=headers)
                response=connection.getresponse()
                self.send_response(response.status)
                self.send_header('Content-Type',response.getheader('Content-Type','text/event-stream'))
                self.send_header('Connection','close');self.end_headers()
                if response.status==200:
                    with lock:facts['forwardedResponses']+=1
                while chunk:=response.read1(8192):self.wfile.write(chunk);self.wfile.flush()
            except (OSError,http.client.HTTPException):
                with lock:facts['relayFailure']=True
            finally:connection.close();self.close_connection=True
    server=ThreadingHTTPServer(('127.0.0.1',0),Handler);server.daemon_threads=True
    thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
    try:yield f'http://127.0.0.1:{server.server_port}',facts
    finally:server.shutdown();server.server_close();thread.join(timeout=3)
