"""Exercise only the new driver's own indicator, plus an owned fixture for blocking capture."""
import ctypes as C,json,queue,subprocess,threading,time,sys
from ctypes import wintypes as W
from pathlib import Path
SOURCE=Path(__file__).resolve().parent.parent
ROOT=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else SOURCE/'results/indicator'
ROOT.mkdir(parents=True,exist_ok=True)
EXE=SOURCE/'target/release/omnicore-desktop.exe'
U=C.WinDLL('user32',use_last_error=True)
def sig(name,args,result):
    fn=getattr(U,name);fn.argtypes=args;fn.restype=result;return fn
CALLBACK=C.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
sig('SetProcessDpiAwarenessContext',[C.c_void_p],W.BOOL)(C.c_void_p(-4))
sig('EnumWindows',[CALLBACK,W.LPARAM],W.BOOL);sig('EnumChildWindows',[W.HWND,CALLBACK,W.LPARAM],W.BOOL)
sig('GetWindowThreadProcessId',[W.HWND,C.POINTER(W.DWORD)],W.DWORD)
sig('GetWindowTextW',[W.HWND,W.LPWSTR,C.c_int],C.c_int);sig('GetClassNameW',[W.HWND,W.LPWSTR,C.c_int],C.c_int)
sig('IsWindowVisible',[W.HWND],W.BOOL);sig('SendMessageW',[W.HWND,W.UINT,W.WPARAM,W.LPARAM],W.LPARAM)
sig('PostMessageW',[W.HWND,W.UINT,W.WPARAM,W.LPARAM],W.BOOL)
sig('GetLayeredWindowAttributes',[W.HWND,C.POINTER(W.DWORD),C.POINTER(C.c_ubyte),C.POINTER(W.DWORD)],W.BOOL)
sig('GetWindowDisplayAffinity',[W.HWND,C.POINTER(W.DWORD)],W.BOOL)
sig('GetForegroundWindow',[],W.HWND);sig('GetDesktopWindow',[],W.HWND)
def wait(fn,seconds=8):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            result=fn()
            if result:return result
        except (OSError,ValueError,KeyError):pass
        time.sleep(.04)
    raise AssertionError('Timed out waiting for own indicator')
def text(h,kind='GetWindowTextW'):
    value=C.create_unicode_buffer(512);getattr(U,kind)(h,value,len(value));return value.value
def windows(pid):
    result=[]
    @CALLBACK
    def collect(h,_):
        owner=W.DWORD();U.GetWindowThreadProcessId(h,C.byref(owner))
        if owner.value==pid and text(h,'GetClassNameW')=='OmniCoreDesktopIndicator':result.append(int(h))
        return True
    U.EnumWindows(collect,0);return result
def button(parent):
    found=[]
    @CALLBACK
    def collect(h,_):
        if text(h,'GetClassNameW').lower()=='button':found.append(int(h))
        return True
    U.EnumChildWindows(parent,collect,0);return found[0] if len(found)==1 else None
class Client:
    def __init__(self,locale='es'):
        self.p=subprocess.Popen([str(EXE),'--allow-input','--locale',locale,'--output-dir',str(ROOT/'captures')],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=0x08000000)
        self.lines=queue.Queue();self.errors=queue.Queue();self.id=0
        threading.Thread(target=lambda:[self.lines.put(line) for line in self.p.stdout],daemon=True).start()
        threading.Thread(target=lambda:[self.errors.put(line.strip()) for line in self.p.stderr],daemon=True).start()
        self.handles=wait(lambda:windows(self.p.pid) if len(windows(self.p.pid))==2 else None)
        self.banner=next(h for h in self.handles if button(h));self.curtain=next(h for h in self.handles if h!=self.banner)
        wait(lambda:all(U.IsWindowVisible(h) for h in self.handles))
    def send(self,method,params=None):
        self.id+=1;self.p.stdin.write(json.dumps({'id':self.id,'method':method,'params':params or {}})+'\n');self.p.stdin.flush()
    def call(self,method,params=None):self.send(method,params);return json.loads(self.lines.get(timeout=12))
    def close(self):
        self.p.stdin.close()
        try:self.p.wait(timeout=4)
        except subprocess.TimeoutExpired:self.p.kill();self.p.wait();raise AssertionError('Indicator did not close on EOF')
        assert not windows(self.p.pid),'Indicator window leaked'
report={'checks':{}}
for mode in ['button','escape_message']:
    c=Client();foreground=U.GetForegroundWindow()
    try:
        assert foreground not in c.handles,'Indicator stole focus'
        color=W.DWORD();alpha=C.c_ubyte();flags=W.DWORD()
        assert U.GetLayeredWindowAttributes(c.curtain,C.byref(color),C.byref(alpha),C.byref(flags))
        assert alpha.value==16
        affinity=W.DWORD();assert U.GetWindowDisplayAffinity(c.curtain,C.byref(affinity));assert affinity.value==0x11
        listing=c.call('list_windows');assert listing['ok'];assert not any(w['pid']==c.p.pid for w in listing['result'])
        denied=c.call('get_window_state',{'id':hex(U.GetDesktopWindow())})
        assert denied['error']['code']=='WINDOW_REQUIRED'
        denied=c.call('get_window_state',{'id':hex(c.banner)})
        assert denied['error']['code']=='WINDOW_REQUIRED'
        began=time.monotonic()
        if mode=='button':U.SendMessageW(button(c.banner),0xf5,0,0)
        else:U.PostMessageW(c.banner,0x312,1,0) # Exercise the registered hotkey message without sending Esc to other apps.
        assert c.errors.get(timeout=2)=='OMNICORE_DESKTOP_CANCELLED'
        wait(lambda:not any(U.IsWindowVisible(h) for h in c.handles),2)
        denied=c.call('capabilities');assert denied['error']['code']=='CANCELLED'
        report['checks'][mode+'_cancel']={'latched':True,'latency_seconds':round(time.monotonic()-began,3)}
    finally:c.close()
report['checks'].update(indicator_does_not_take_focus=True,alpha_16_of_255=True,excluded_from_capture=True,
    desktop_and_own_overlay_capture_rejected=True,own_overlay_absent_from_discovery=True,windows_removed_on_eof=True)
# Cancel a blocked PrintWindow worker and verify the UI remains responsive.
state=ROOT/'fixture-state.json';command=ROOT/'fixture-command.json';ack=Path(str(command)+'.ack')
for file in[state,command,ack]:file.unlink(missing_ok=True)
fixture=subprocess.Popen(['powershell.exe','-NoProfile','-STA','-WindowStyle','Hidden','-File',
    str(SOURCE/'tests/fixture.ps1'),'-StatePath',str(state),'-CommandPath',str(command)],
    stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=0x08000000)
c=None
try:
    data=wait(lambda:json.loads(state.read_text(encoding='utf-8-sig')),20);assert data['pid']==fixture.pid
    c=Client();target=c.call('get_window',{'id':data['hwnd']})['result']
    command.write_text('{"command":"freeze"}');wait(ack.exists)
    c.send('get_window_state',{'window':target,'include_screenshot':True,'include_text':False,'capture_backend':'printwindow'})
    time.sleep(.2);began=time.monotonic();U.SendMessageW(button(c.banner),0xf5,0,0)
    answer=json.loads(c.lines.get(timeout=2));assert answer['error']['code']=='CANCELLED',answer
    assert c.errors.get(timeout=2)=='OMNICORE_DESKTOP_CANCELLED'
    report['checks']['blocked_capture_cancelled_promptly']={'seconds':round(time.monotonic()-began,3)}
finally:
    if c:c.close()
    command.write_text('{"command":"close"}')
    try:fixture.wait(timeout=13)
    except subprocess.TimeoutExpired:fixture.kill();fixture.wait()
report['all_passed']=True
(ROOT/'indicator-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
