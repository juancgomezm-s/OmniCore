"""Live integration test; only operates on its own, visibly labelled WinForms fixture."""
import base64
import ctypes
import io
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
from PIL import Image

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
import fixture_metadata as native

def wait_for(fn,timeout=12):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        try:
            result=fn()
            if result: return result
        except (ValueError,OSError,KeyError):
            pass
        time.sleep(.1)
    raise AssertionError('Timed out waiting for controlled fixture state')

class Client:
    def __init__(self, output, input_enabled=True, ttl=30):
        args=[str(ROOT/'target/release/omnicore-desktop.exe'),'--output-dir',str(output),'--observation-ttl',str(ttl)]
        if input_enabled: args.append('--allow-input')
        self.process=subprocess.Popen(args,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                                      text=True,encoding='utf-8',creationflags=0x08000000)
        self.queue=queue.Queue()
        self.next_id=0
        threading.Thread(target=self._read,daemon=True).start()
    def _read(self):
        for line in self.process.stdout: self.queue.put(line)
        self.queue.put(None)
    def call(self,method,params=None,ok=True):
        self.next_id+=1
        if method=='get_window_state': params=dict(params or {},capture_backend='printwindow')
        self.process.stdin.write(json.dumps({'id':self.next_id,'method':method,'params':params or {}},ensure_ascii=False)+'\n')
        self.process.stdin.flush()
        line=self.queue.get(timeout=30)
        assert line, 'Server exited unexpectedly'
        answer=json.loads(line)
        assert answer['id']==self.next_id
        if ok: assert answer['ok'],f'{method}: {answer.get("error")}'
        return answer['result'] if ok else answer
    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try: self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill();self.process.wait()

def main():
    output=Path(sys.argv[1]).resolve()
    output.mkdir(parents=True,exist_ok=True)
    state_path=output/'fixture-state.json'
    command_path=output/'fixture-command.json'
    state_path.unlink(missing_ok=True);command_path.unlink(missing_ok=True)
    log=(output/'fixture.log').open('w',encoding='utf-8')
    fixture=subprocess.Popen(['powershell.exe','-NoProfile','-STA','-WindowStyle','Hidden',
                              '-File',str(ROOT/'tests/fixture.ps1'),'-StatePath',str(state_path),'-CommandPath',str(command_path)],
                             stdout=log,stderr=log,creationflags=0x08000000)
    clients=[]
    report={'original_executable_used':False,'target':'own WinForms test window','checks':{}}
    def state():
        return json.loads(state_path.read_text(encoding='utf-8-sig'))
    try:
        fixture_state=wait_for(state,25)
        assert fixture_state['pid']==fixture.pid
        target_id=fixture_state['hwnd']
        readonly=Client(output/'disabled-input',False);clients.append(readonly)
        denied=readonly.call('activate_window',{'id':target_id},ok=False)
        assert denied['error']['code']=='INPUT_DISABLED'
        report['checks']['input_disabled_by_default']=True
        client=Client(output/'captures');clients.append(client)
        found=client.call('list_windows')
        target=next(x for x in found if x['id']==target_id)
        assert target['pid']==fixture.pid
        client.call('activate_window',{'window':target})
        def observe(text=True,image=False):
            return client.call('get_window_state',{'window':target,'include_text':text,'include_screenshot':image})
        observation=observe(True,True)
        png=base64.b64decode(observation['screenshots'][0]['url'].split(',',1)[1])
        im=Image.open(io.BytesIO(png));im.load()
        assert (observation['screenshots'][0]['width'],observation['screenshots'][0]['height'])==im.size
        assert not observation['screenshots'][0].get('all_black',False)
        region=native.describe(int(state()['controls']['drag']['hwnd'],16))['bounds']
        root_bounds=observation['window']['bounds']
        sample=(round(region['x']+.9*region['width']-root_bounds['x']),round(region['y']+.9*region['height']-root_bounds['y']))
        color=im.convert('RGB').getpixel(sample)
        assert max(abs(a-b) for a,b in zip(color,(176,196,222)))<=5,('DPI capture alignment',sample,color)
        report['checks']['dpi_capture_coordinate_alignment']={'passed':True,'sample':list(sample),'rgb':list(color)}
        report['checks']['target_window_png']={'passed':True,'size':list(im.size),'bytes':len(png)}
        uia=observation['accessibility']
        assert len(uia['elements'])>3
        assert 'Fixture-Secret-DoNotExpose' not in json.dumps(uia,ensure_ascii=False)
        assert any(e.get('is_password') for e in uia['elements'])
        entry=next(e for e in uia['elements'] if e.get('name')=='Texto de prueba')
        report['checks']['uia_tree_and_password_redaction']={'passed':True,'elements':len(uia['elements'])}
        client.call('click',{'window':target,'observation_id':observation['observation_id'],'element_index':entry['index']})
        stale=client.call('type_text',{'window':target,'observation_id':observation['observation_id'],'text':'not sent'},ok=False)
        assert stale['error']['code']=='STALE_OBSERVATION'
        report['checks']['observation_invalidated_after_input']=True
        observation=observe()
        expected='Prueba áñ漢字🙂'
        client.call('type_text',{'window':target,'observation_id':observation['observation_id'],'text':expected})
        wait_for(lambda: state()['text']==expected)
        observation=observe()
        assert expected in json.dumps(observation['accessibility'],ensure_ascii=False)
        report['checks']['unicode_input_and_uia_readback']=True
        client.call('press_key',{'window':target,'observation_id':observation['observation_id'],'key':'CTRL+A'})
        observation=observe()
        client.call('type_text',{'window':target,'observation_id':observation['observation_id'],'text':'Reemplazado'})
        wait_for(lambda: state()['text']=='Reemplazado')
        observation=observe()
        client.call('press_key',{'window':target,'observation_id':observation['observation_id'],'key':'ENTER'})
        wait_for(lambda: state()['enter_keys']>0)
        report['checks']['key_chord_and_enter']=True
        observation=observe()
        button=next(e for e in observation['accessibility']['elements'] if e.get('name')=='Contar clic')
        client.call('click',{'window':target,'observation_id':observation['observation_id'],'element_index':button['index']})
        wait_for(lambda: state()['clicks']>=1)
        report['checks']['uia_index_click']=True
        def coordinates(control,dx=.5,dy=.5):
            b=native.describe(int(state()['controls'][control]['hwnd'],16))['bounds']
            w=native.describe(int(target_id,16))['bounds']
            return round(b['x']+b['width']*dx-w['x']),round(b['y']+b['height']*dy-w['y'])
        observation=observe(False,True)
        x1,y1=coordinates('drag',.2,.4);x2,y2=coordinates('drag',.7,.6)
        client.call('drag',{'window':target,'observation_id':observation['observation_id'],
                            'start_x':x1,'start_y':y1,'end_x':x2,'end_y':y2,'duration':.35})
        wait_for(lambda:state()['drag_distance']>50)
        report['checks']['mouse_drag']=True
        observation=observe(False,True)
        x,y=coordinates('wheel',.9,.5)
        client.call('click',{'window':target,'observation_id':observation['observation_id'],'x':x,'y':y})
        observation=observe(False,True)
        client.call('scroll',{'window':target,'observation_id':observation['observation_id'],'x':x,'y':y,'delta':-360})
        wait_for(lambda: state()['wheels']>0 or state()['scrollY']<0)
        report['checks']['mouse_wheel']=True
        observation=observe(False,True)
        rejected=client.call('click',{'window':target,'observation_id':observation['observation_id'],'x':-100,'y':-100},ok=False)
        assert rejected['error']['code']=='OUTSIDE_TARGET'
        rejected=client.call('click',{'window':target,'observation_id':observation['observation_id'],
                                      'screenshot_id':'not-this-image','x':10,'y':10},ok=False)
        assert rejected['error']['code']=='STALE_SCREENSHOT'
        report['checks']['out_of_window_and_stale_screenshot_rejected']=True
        command_path.write_text(json.dumps({'command':'move'}),encoding='utf-8')
        wait_for(lambda:native.describe(int(target_id,16))['bounds']!=observation['window']['bounds'])
        rejected=client.call('press_key',{'window':target,'observation_id':observation['observation_id'],'key':'ENTER'},ok=False)
        assert rejected['error']['code']=='WINDOW_CHANGED'
        report['checks']['geometry_change_rejected']=True
        observation=observe(False,True)
        command_path.write_text(json.dumps({'command':'show_other'}),encoding='utf-8')
        wait_for(lambda:native.U.GetForegroundWindow()!=int(target_id,16))
        denied=client.call('type_text',{'window':target,'observation_id':observation['observation_id'],'text':'not sent'},ok=False)
        assert denied['error']['code']=='FOCUS_CHANGED'
        report['checks']['focus_change_rejected']=True
        command_path.write_text(json.dumps({'command':'hide_other'}),encoding='utf-8')
        time.sleep(.3)
        short=Client(output/'expiry',True,.1);clients.append(short)
        own=short.call('get_window',{'id':target_id})
        short.call('activate_window',{'window':own})
        previous=short.call('get_window_state',{'window':own,'include_text':False,'include_screenshot':True})
        time.sleep(.2)
        expired=short.call('press_key',{'window':own,'observation_id':previous['observation_id'],'key':'ENTER'},ok=False)
        assert expired['error']['code']=='STALE_OBSERVATION'
        report['checks']['expired_observation_rejected']=True
        command_path.write_text(json.dumps({'command':'freeze'}),encoding='utf-8')
        wait_for(lambda:Path(str(command_path)+'.ack').exists())
        stalled=client.call('get_window_state',{'window':target,'include_text':False,'include_screenshot':True},ok=False)
        assert stalled['error']['code']=='CAPTURE_TIMEOUT',stalled
        assert client.call('capabilities')['protocol']=='ndjson-v2-independent'
        report['checks']['capture_timeout_and_server_survives']=True
        report['all_passed']=True
    except Exception as exc:
        report['all_passed']=False
        report['error']=str(exc)
        raise
    finally:
        for client in clients:client.close()
        command_path.write_text(json.dumps({'command':'close'}),encoding='utf-8')
        try:fixture.wait(timeout=8)
        except subprocess.TimeoutExpired:
            fixture.kill();fixture.wait()
        log.close()
        report['fixture_closed']=fixture.poll() is not None
        (output/'verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps(report,ensure_ascii=False,indent=2))

if __name__=='__main__':main()
