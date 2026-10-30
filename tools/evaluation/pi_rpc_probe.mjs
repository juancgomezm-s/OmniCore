// Read-only use of OmniCoder's existing PiRpcClient and the installed Pi runtime.
// All sessions/configuration/output live in the explicit disposable run directory.
import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
const request=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));
const {PiRpcClient,PiUiAnswer}=await import(pathToFileURL(request.clientModule).href);
const raw=fs.createWriteStream(path.join(request.output,'pi-events.jsonl'),{flags:'wx'});
let settle;
const settled=new Promise(resolve=>{settle=resolve});
const errors=[];
const client=new PiRpcClient({command:request.node,args:[request.cli,'--mode','rpc','--provider','openai-codex','--model',request.model,
  '--thinking',request.thinking ?? 'medium','--no-extensions','--no-skills','--no-prompt-templates','--no-themes','--no-mcp','--no-context-files','--no-approve',
  '--tools','read,edit,write,find,grep,ls'+(request.allowDelegate?',delegate':''),'--offline','--extension',path.join(import.meta.dirname,'pi_guard.mjs'),
  ...(request.resumeSession?['--session',request.resumeSession]:[]),
  '--session-dir',path.join(request.output,'sessions')],cwd:request.workspace,env:{...process.env,PI_CODING_AGENT_DIR:request.agentDir,
  M10_BENCHMARK_ROOT:request.workspace,M10_DENY_WRITES:request.denyWrites?'1':'0',M10_ALLOW_DELEGATE:request.allowDelegate?'1':'0',
  M10_REQUEST_FILE:path.resolve(process.argv[2]),M10_PROBE_MODULE:import.meta.filename,M10_INTERRUPT_FIRST:request.interruptFirst?'1':'0',M10_TOOL_LIMIT:String(request.toolLimit??32)}},
 {onRawLine:line=>raw.write(line+'\n'),onAgentSettled:()=>settle(),onAgentError:(_message,source)=>errors.push(source)});
client.uiPresenter=async()=>PiUiAnswer.cancel();
client.commandTimeoutMs=120000;
let timer;
try{
  await client.start();
  const start=performance.now();
  const ack=await client.sendPrompt(request.objective);
  if(!ack.success)throw new Error('prompt_rejected');
  await Promise.race([settled,new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('deadline')),request.timeoutMs??240000)})]);
  const messages=await client.getMessages();
  const stats=await client.getSessionStats();
  if(fs.existsSync(path.join(request.output,'budget-stop.json')))throw new Error('explicit_budget_exhausted');
  const result={schema:'omnicore.pi-agentic-raw.v1',status:'completed',elapsedSeconds:(performance.now()-start)/1000,errors,messages,stats};
  fs.writeFileSync(path.join(request.output,'pi-result.json'),JSON.stringify(result,null,2));
  process.stdout.write(JSON.stringify({status:result.status,errors:errors.length})+'\n');
}catch(_error){
  fs.writeFileSync(path.join(request.output,'pi-result.json'),JSON.stringify({schema:'omnicore.pi-agentic-raw.v1',status:'error',errors},null,2));
  process.exitCode=2;
}finally{
  clearTimeout(timer);
  await client.dispose();
  await new Promise(resolve=>raw.end(resolve));
}
