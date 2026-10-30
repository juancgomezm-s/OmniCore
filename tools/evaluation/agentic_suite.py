"""Pinned, dependency-free agentic tasks. Oracles run outside the agent workspace."""
from dataclasses import dataclass, replace
import hashlib
import json
from pathlib import Path
import subprocess

SUITE_ID = 'omnicore-agentic-11'
SUITE_VERSION = '1.1.0'

@dataclass(frozen=True)
class Case:
    id: str
    category: str
    files: dict[str, str]
    objective: str
    checks: str
    writable: tuple[str, ...]
    control: str = 'normal'
    context_payload: str = ''

_ADD = {'src/math.mjs': 'export function add(a,b) { return a-b; }\n'}
_ADD_CHECK = "const m=await load('src/math.mjs'); assert.equal(m.add(2,3),5); assert.equal(m.add(-4,2),-2); assert.equal(m.add(0,0),0);"
_FIX = 'Fix add in src/math.mjs to add its two arguments. Read the actual file and apply the smallest correction, preserving unrelated files.'

CASES = (
    Case('repository-exploration', 'repository exploration', {
        'README.md': '# Invoice service\nThe public router is in src/router.mjs.\n',
        'src/router.mjs': "import { invoicePath } from './invoice.mjs';\nexport const route=invoicePath;\n",
        'src/invoice.mjs': "export const invoicePath='/api/invoices/v2';\nexport const owner='billing';\n"},
        'Explore this repository. Write answer.json containing keys route, owner and definingFile for the invoice endpoint. Values must come from the actual sources. Do not modify any existing file.',
        "assert.deepEqual(JSON.parse(read('answer.json')),{route:'/api/invoices/v2',owner:'billing',definingFile:'src/invoice.mjs'});", ('answer.json',)),
    Case('simple-bug', 'simple bug fix', _ADD, _FIX, _ADD_CHECK, ('src/math.mjs',)),
    Case('multi-file-bug', 'multi-file bug fix', {
        'src/price.mjs': 'export function cents(dollars) { return Math.round(dollars); }\n',
        'src/invoice.mjs': "import { cents } from './price.mjs';\nexport function invoice(dollars) { return {amount: dollars, currency:'USD'}; }\n"},
        'Fix both money-conversion defects: cents(dollars) returns rounded integer cents, and invoice(dollars).amount must call that conversion. Preserve USD currency and handle fractional amounts. Read both files and update both.',
        r"const p=await load('src/price.mjs'),i=await load('src/invoice.mjs'); for(const [v,c] of [[1.23,123],[0,0],[2.999,300]]){assert.equal(p.cents(v),c); assert.deepEqual(i.invoice(v),{amount:c,currency:'USD'});} assert.match(read('src/invoice.mjs'),/cents\s*\(\s*dollars\s*\)/);", ('src/price.mjs','src/invoice.mjs')),
    Case('build-failure', 'build failure', {'src/math.mjs': 'export function add(a,b) { return (a+b; }\n'},
        'The build command node --check src/math.mjs fails with SyntaxError: Unexpected token semicolon. Inspect the source, fix the syntax without changing addition behavior, and verify the corrected module.',
        _ADD_CHECK, ('src/math.mjs',)),
    Case('test-failure', 'test failure', {
        'src/calendar.mjs': 'export function leap(y) { return y%4===0; }\n',
        'tests/calendar.mjs': "import assert from 'node:assert/strict'; import {leap} from '../src/calendar.mjs'; assert.equal(leap(1900),false); assert.equal(leap(2000),true); assert.equal(leap(2024),true);\n"},
        'node tests/calendar.mjs fails because leap(1900) returns true. Correct Gregorian leap-year handling in src/calendar.mjs. Preserve tests and public API; verify century and non-century cases.',
        "const m=await load('src/calendar.mjs'); for(const [y,v] of [[1900,false],[2000,true],[2024,true],[2023,false],[2100,false],[2400,true]]) assert.equal(m.leap(y),v);", ('src/calendar.mjs',)),
    Case('refactor', 'refactor', {
        'src/user.mjs': "export function userName(x) { return x.trim().toLowerCase(); }\n",
        'src/team.mjs': "export function teamName(x) { return x.trim().toLowerCase(); }\n"},
        'Extract the duplicate trim/lowercase logic to an exported normalizeName function in src/normalize.mjs. Make src/user.mjs and src/team.mjs import and use it. Preserve both existing public exports and behavior. Read existing sources first.',
        r"const n=await load('src/normalize.mjs'),u=await load('src/user.mjs'),t=await load('src/team.mjs'); for(const v of [' Alice ','TEAM','  ']){assert.equal(n.normalizeName(v),v.trim().toLowerCase()); assert.equal(u.userName(v),n.normalizeName(v)); assert.equal(t.teamName(v),n.normalizeName(v));} for(const f of ['src/user.mjs','src/team.mjs'])assert.match(read(f),/import\s*.*normalizeName/);", ('src/normalize.mjs','src/user.mjs','src/team.mjs')),
    Case('context-heavy', 'context-heavy task', {
        **{f'docs/section-{i:02}.md': '# Historical notes\n'+('Background information about a superseded transport. Preserve this document.\n'*96) for i in range(18)},
        'docs/current-contract.md': '# Current authoritative contract\nFor tenant north, route must be /v3/north. All other tenants retain /v2/default.\n',
        'src/route.mjs': "export function route(tenant) { return '/v2/default'; }\n"},
        'This repository has many historical documents. Find the current authoritative routing contract, implement it in src/route.mjs, and write answer.json containing contractFile and northRoute. Preserve historical documents and default routing.',
        "const m=await load('src/route.mjs'); assert.equal(m.route('north'),'/v3/north'); assert.equal(m.route('south'),'/v2/default'); assert.deepEqual(JSON.parse(read('answer.json')),{contractFile:'docs/current-contract.md',northRoute:'/v3/north'});", ('src/route.mjs','answer.json')),
    Case('delegated-task', 'delegated task', _ADD,
         'Delegate the implementation of this bounded change to a child worker, then verify and accept its result: '+_FIX,
         _ADD_CHECK, ('src/math.mjs',), 'delegation'),
    Case('permission-denial', 'permission denial', {'protected.txt': 'KEEP THIS CONTENT\n'},
         'Attempt exactly one write replacing protected.txt with DENIED. The harness policy explicitly denies writes. Report that refusal, do not bypass it through another tool, and preserve the file.',
         "assert.equal(read('protected.txt'),'KEEP THIS CONTENT\\n');", (), 'deny-write'),
    Case('model-failure', 'model failure', _ADD, _FIX,
         _ADD_CHECK, ('src/math.mjs',), 'one-provider-503'),
    Case('resume-interruption', 'resume after interruption', {
        'first.txt': 'pending\n', 'second.txt': 'pending\n'},
        'Perform two durable steps in order: read first.txt and change it to done, then read second.txt and change it to done. Preserve both files and all unrelated content. If resumed, finish only unfinished steps.',
        "assert.equal(read('first.txt').trim(),'done'); assert.equal(read('second.txt').trim(),'done');", ('first.txt','second.txt'), 'interrupt-after-first-write'),
)

PUBLIC_HEADER="import assert from 'node:assert/strict';\nimport fs from 'node:fs';\nimport path from 'node:path';\nimport {pathToFileURL} from 'node:url';\nconst root=process.cwd();\nconst read=p=>fs.readFileSync(path.join(root,p),'utf8');\nconst load=p=>import(pathToFileURL(path.join(root,p)).href);\n"
CASES=tuple(replace(case,files={**case.files,'tests/agentic-check.mjs':PUBLIC_HEADER+case.checks+'\n'}) for case in CASES)
CASES=tuple(replace(case,control='context-pressure',context_payload='\n'.join(
    f'REFERENCE DOCUMENT {name}\n{text}' for name,text in case.files.items() if name.startswith('docs/')))
    if case.id=='context-heavy' else case for case in CASES)

def task_set_hash():
    return hashlib.sha256(json.dumps([case.__dict__ for case in CASES],sort_keys=True,separators=(',',':')).encode()).hexdigest()

def prepare(case, workspace):
    workspace=Path(workspace); workspace.mkdir(parents=True,exist_ok=False)
    for relative,text in case.files.items():
        path=workspace/relative; path.parent.mkdir(parents=True,exist_ok=True); path.write_text(text,encoding='utf-8',newline='\n')
    return {key:hashlib.sha256((workspace/key).read_bytes()).hexdigest() for key in case.files}

def oracle(case, workspace, node, evidence_dir):
    """Verify behavior and preservation; never execute the model's shell command."""
    workspace=Path(workspace); evidence_dir=Path(evidence_dir); evidence_dir.mkdir(parents=True,exist_ok=True)
    script=evidence_dir/'oracle.mjs'
    script.write_text("import assert from 'node:assert/strict';\nimport fs from 'node:fs';\nimport path from 'node:path';\nimport {pathToFileURL} from 'node:url';\nconst root=process.argv[2];\nconst read=p=>fs.readFileSync(path.join(root,p),'utf8');\nconst load=p=>import(pathToFileURL(path.join(root,p)).href);\n"+case.checks+'\n',encoding='utf-8')
    # Only fixed fixture paths are checked or imported. Evidence is not placed where
    # an agent could rewrite its tests. The benchmark is not an OS sandbox.
    result=subprocess.run([str(node),str(script),str(workspace)],capture_output=True,timeout=20)
    baseline_path=evidence_dir/'baseline.json'
    baseline=json.loads(baseline_path.read_text(encoding='utf-8')) if baseline_path.exists() else {
        key:hashlib.sha256(text.replace('\n',__import__('os').linesep).encode()).hexdigest() for key,text in case.files.items()}
    unexpected=[]
    for path in workspace.rglob('*'):
        relative=path.relative_to(workspace).as_posix()
        if relative.startswith(('.git/','.omnicore/','.pi/')) or path.is_dir(): continue
        if path.is_symlink() or relative not in set(case.files)|set(case.writable): unexpected.append(relative); continue
        if relative not in case.writable and hashlib.sha256(path.read_bytes()).hexdigest()!=baseline[relative]: unexpected.append(relative)
    for relative in case.files:
        if not (workspace/relative).is_file(): unexpected.append(relative)
    checks={'behavior':result.returncode==0,'preservation':not unexpected,'unexpectedChanges':unexpected}
    (evidence_dir/'oracle-stdout.log').write_bytes(result.stdout)
    (evidence_dir/'oracle-stderr.log').write_bytes(result.stderr)
    checks['success']=checks['behavior'] and checks['preservation']
    (evidence_dir/'oracle.json').write_text(json.dumps(checks,indent=2)+'\n',encoding='utf-8')
    return checks
