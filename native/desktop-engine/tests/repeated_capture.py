from pathlib import Path
import argparse,base64,datetime,io,json,subprocess,time
from PIL import Image
parser=argparse.ArgumentParser();parser.add_argument('--binary',type=Path,required=True);parser.add_argument('--fixture',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--mode',choices=['claude','omni'],required=True);a=parser.parse_args();a.output.mkdir(parents=True,exist_ok=True)
state_path=a.output/'fixture-state.json';state_path.unlink(missing_ok=True)
f=subprocess.Popen([str(a.fixture),str(state_path)],creationflags=0x08000000)
p=None;counter=0;started=datetime.datetime.now(datetime.timezone.utc).isoformat()
try:
    until=time.monotonic()+15
    while True:
        try:
            state=json.loads(state_path.read_text());assert state['pid']==f.pid;break
        except (OSError,ValueError,AssertionError):
            if time.monotonic()>until:raise
            time.sleep(.05)
    args=[str(a.binary)]+(['serve'] if a.mode=='claude' else ['--output-dir',str(a.output/'captures')])
    p=subprocess.Popen(args,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=0x08000000)
    def call(method,params):
        global counter
        counter+=1;p.stdin.write(json.dumps({'id':counter,'method':method,'params':params})+'\n');p.stdin.flush();r=json.loads(p.stdout.readline());assert r['ok'],r;return r['result']
    target=next(w for w in call('list_windows',{}) if w['pid']==f.pid)
    if a.mode=='claude':target={k:target[k] for k in ('id','pid','app')}
    sizes=[]
    for i in range(12):
        scale=[1.0,.8,.5,.1][i%4]
        result=call('get_window_state',{'window':target,'include_text':False,'include_screenshot':True,'capture_backend':'wgc','image_scale':scale})
        shot=result['screenshots'][0];image=Image.open(io.BytesIO(base64.b64decode(shot['url'].split(',',1)[1])));image.load()
        assert image.size==(shot['width'],shot['height']);assert shot['capture_scope']=='window'
        assert image.width<=shot['physicalWidth'] and image.height<=shot['physicalHeight'];sizes.append(list(image.size))
    p.stdin.close();p.wait(timeout=5);assert p.returncode==0,p.stderr.read()
    report={'mode':a.mode,'repeated_wgc_captures':12,'scales':[1,.8,.5,.1],'sizes':sizes,'started_at_utc':started,'all_passed':True};(a.output/'report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
finally:
    if p and p.poll() is None:p.kill();p.wait()
    f.terminate();f.wait()
