"""Finalize settled agentic receipts without launching a model or changing a workspace."""
from collections import defaultdict
from contextlib import closing
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess

from agentic_suite import CASES, SUITE_ID, SUITE_VERSION, task_set_hash
from accepted_result import result_oracle
from run_agentic import POLICY, objective
import compare
import measure

def sha(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def read(path):return json.loads(Path(path).read_text(encoding='utf-8'))
def write(path,value):Path(path).write_text(json.dumps(value,indent=2,allow_nan=False)+'\n',encoding='utf-8')

def core_details(evidence):
    snapshot=evidence/'journal-snapshot.db'
    with closing(sqlite3.connect(snapshot.as_uri()+'?mode=ro&immutable=1',uri=True)) as db:
        rows=[(kind,json.loads(payload),lane,turn,seq,version) for kind,payload,lane,turn,seq,version in db.execute(
            'select event_type,payload,lane_id,turn_id,seq,schema_version from events order by seq')]
    groups=defaultdict(list);incoming=[];outgoing=[];models=set();reasoning=set()
    for kind,body,lane,turn,seq,version in rows:
        if kind=='model_step.started':
            models.add(body['ModelId'])
            applied=(body.get('ReasoningResolution') or {}).get('AppliedRequest')
            reasoning.add(applied.get('Kind') if isinstance(applied,dict) else body.get('ReasoningKind'))
        if kind in ('model_step.completed','meta_model.invocation_completed','meta_model.invocation_failed'):
            fields=body.get('ReportedUsageFields');usage=body.get('Usage')
            if type(fields) is int and isinstance(usage,dict):
                if fields&1:incoming.append(usage['Input']);groups[lane or turn].append(usage['Input'])
                if fields&2:outgoing.append(usage['Output'])
    compactions=sum(kind=='context.checkpoint_recorded' for kind,*_ in rows)
    escalations=sum(kind=='model.escalation_completed' for kind,*_ in rows)
    usage=measure.invocation_metrics([{'type':kind,'body':body,'lane':lane,'turn':turn,'seq':seq,'schema':version}
        for kind,body,lane,turn,seq,version in rows])
    return {'inputTokensObserved':sum(incoming) if incoming else None,
        'outputTokensObserved':sum(outgoing) if outgoing else None,
        'peakRequestInputTokensObserved':max(incoming) if incoming else None,
        'contextGrowthObserved':max((max(v)-v[0] for v in groups.values()),default=None),
        'contextCompactions':compactions,'modelEscalations':escalations,
        'artifactsExternalized':None,'externalizationCoverage':'not-exposed-as-a-canonical-count',
        'modelsRequested':sorted(models),'reasoningRequested':sorted(reasoning,key=lambda x:x or ''),
        'tokensThroughCheckpoint':usage['tokens'],'usageCoverageComplete':usage['usageCoverageComplete'],
        'journalSha256':sha(snapshot),'events':len(rows)}

def pi_details(evidence):
    selected=evidence/'resumed/pi-result.json'
    if not selected.exists():selected=evidence/'pi-result.json'
    data=read(selected)
    messages=json.loads(data.get('messages',{}).get('dataJson','{}')).get('messages',[])
    groups=[];inputs=[];outputs=[];models=set();reasoning=set()
    def collect(result):
        local=[]
        for message in json.loads(result.get('messages',{}).get('dataJson','{}')).get('messages',[]):
            if message.get('role')!='assistant':continue
            if message.get('model'):models.add(message['model'])
            if message.get('thinkingLevel'):reasoning.add(message['thinkingLevel'])
            usage=message.get('usage')
            if isinstance(usage,dict) and message.get('stopReason')!='error':
                value=usage['input']+usage.get('cacheRead',0)+usage.get('cacheWrite',0)
                local.append(value);inputs.append(value);outputs.append(usage['output'])
        if local:groups.append(local)
    collect(data)
    for child in evidence.glob('child-*/pi-result.json'):collect(read(child))
    compactions=0
    for event_file in [evidence/'pi-events.jsonl',*evidence.glob('child-*/pi-events.jsonl'),*evidence.glob('resumed/pi-events.jsonl')]:
        for line in event_file.read_text(encoding='utf-8').splitlines():
            try:event=json.loads(line)
            except ValueError:continue
            if event.get('type')=='auto_compaction_end' and event.get('result'):compactions+=1
    return {'inputTokensObserved':sum(inputs) if inputs else None,'outputTokensObserved':sum(outputs) if outputs else None,
        'peakRequestInputTokensObserved':max(inputs) if inputs else None,
        'contextGrowthObserved':max((max(v)-v[0] for v in groups),default=None),
        'contextCompactions':compactions,'modelEscalations':0,'artifactsExternalized':None,
        'externalizationCoverage':'not-exposed-by-this-pi-rpc-client',
        'modelsRequested':sorted(models),'reasoningRequested':sorted(reasoning),
        'sourceResultSha256':sha(selected),'messagesObserved':len(messages)}

COPY={
 'es':('Batería agentic de M10','Escenario','Resultado Core / Pi','Tiempo Core / Pi (s)','Tokens Core / Pi','Aprobado','Falló','Desconocido',
       'Una muestra por escenario; costo de suscripción no observado. Identidad declarada, sin autenticar procedencia.'),
 'en':('M10 agentic battery','Scenario','Core / Pi result','Core / Pi time (s)','Core / Pi tokens','Passed','Failed','Unknown',
       'One sample per scenario; subscription charge not observed. Declared identity; provenance is not authenticated.'),
 'fr':('Batterie agentic M10','Scénario','Résultat Core / Pi','Durée Core / Pi (s)','Tokens Core / Pi','Réussi','Échec','Inconnu',
       'Un échantillon par scénario ; coût de l’abonnement non observé. Identité déclarée, provenance non authentifiée.'),
 'it':('Batteria agentic M10','Scenario','Esito Core / Pi','Tempo Core / Pi (s)','Token Core / Pi','Superato','Fallito','Sconosciuto',
       'Un campione per scenario; costo dell’abbonamento non osservato. Identità dichiarata, provenienza non autenticata.')}

def render(core,pi,locale):
    title,scenario,outcome,timing,tokens,passed,failed,missing,note=COPY[locale]
    lines=['# '+title,'',note,'',f'| {scenario} | {outcome} | {timing} | {tokens} |','|---|---|---:|---:|']
    for a,b in zip(core['tasks'],pi['tasks']):
        number=lambda x:missing if x is None else format(x,'.6g')
        lines.append('| '+a['id']+' | '+(passed if a['success'] else failed)+' / '+(passed if b['success'] else failed)
            +' | '+number(a['timeSeconds'])+' / '+number(b['timeSeconds'])+' | '+number(a['tokens'])+' / '+number(b['tokens'])+' |')
    return '\n'.join(lines)+'\n'

def finalize(settings):
    root=Path(settings['output']);report=root/'report';report.mkdir(exist_ok=False)
    private=Path(settings['evaluationRoot']).resolve()
    expected=[case.id for case in CASES];arms={};baselines={};source_index={}
    for arm in ('core','pi'):
        source_root=Path(settings.get('sourceRoot',root))
        source=read(source_root/arm/'measurements.json')
        if {r['id'] for r in source['tasks']}!=set(expected):raise ValueError('incomplete_task_coverage')
        tasks=[];baselines[arm]={}
        for case in CASES:
            directory=Path(settings.get('caseSources',{}).get(arm,{}).get(case.id,source_root/arm/case.id))
            if not directory.resolve().is_relative_to(private):raise ValueError('foreign_case_receipt')
            evidence=directory/'evidence'
            original=read(evidence/'result.json');baselines[arm][case.id]=read(evidence/'baseline.json')
            if read(evidence/'input.json')['objective']!=objective(case):raise ValueError('input_guidance_changed')
            final=report/'oracles'/arm/case.id;final.mkdir(parents=True,exist_ok=False)
            write(final/'baseline.json',baselines[arm][case.id])
            control=original['observations']['control']
            checks,binding=result_oracle(case,directory/'workspace',settings['node'],final,evidence,private,
                delegated=arm=='core' and case.control=='delegation' and original['observations']['runtime']['exitCode']==0)
            if binding:control={**control,'satisfied':True,'verifiedBinding':binding}
            details=(core_details if arm=='core' else pi_details)(evidence)
            success=checks['success'] and original['observations']['runtime']['exitCode']==0 and control['satisfied'] and original['observations'].get('gateUnchanged',True)
            if details['modelsRequested'] and details['modelsRequested']!=['gpt-6.1-sol']:raise ValueError('model_changed')
            if any(level!='medium' for level in details['reasoningRequested']):raise ValueError('reasoning_changed')
            task={key:original.get(key) for key in ('id','timeSeconds','turns','toolCalls','tokens','frontierEscalations','costUsd')}
            if arm=='core' and success:task['tokens']=details['tokensThroughCheckpoint']
            task.update(success=success,contextGrowthTokens=details['contextGrowthObserved'] if task.get('tokens') is not None else None,
                observations=details,control=control,
                runtimeExitCode=original['observations']['runtime']['exitCode'],
                sourceResultSha256=sha(evidence/'result.json'),oracleEvidenceSha256=sha(final/'oracle.json'),
                inputEvidenceSha256=sha(evidence/'input.json'))
            tasks.append(task)
            source_index[f'{arm}/{case.id}']={'result':sha(evidence/'result.json'),'baseline':sha(evidence/'baseline.json'),
                'finalOracle':sha(final/'oracle.json')}
        source['tasks']=tasks;source['measurementPolicy']=POLICY
        source['benchmarkIdentity'].update(suiteId=SUITE_ID,suiteVersion=SUITE_VERSION,taskSetHash=task_set_hash())
        source['caseReceiptReuse']='Each selected receipt must match the exact pinned input and baseline; raw prior versions remain unchanged.'
        compare.validate(source);write(report/(arm+'.json'),source);arms[arm]=source
    if baselines['core']!=baselines['pi']:raise ValueError('baseline_bytes_differ')
    comparison=compare.compare(arms['core'],arms['pi']);write(report/'comparison.json',comparison)
    gate={'schema':'omnicore.agentic-acceptance.v1','suiteId':SUITE_ID,'suiteVersion':SUITE_VERSION,'taskSetHash':task_set_hash(),
        'caseCount':len(CASES),'sameBaselineBytes':True,
        'corePassed':sum(t['success'] for t in arms['core']['tasks']),
        'piPassed':sum(t['success'] for t in arms['pi']['tasks']),
        'allControlsExercised':all(t['control']['satisfied'] for arm in arms.values() for t in arm['tasks']),
        'consumerUiAcceptance':False,'singleSamplePerScenario':True,'newCiAcceptance':False}
    gate['coreBatteryAccepted']=gate['corePassed']==len(CASES) and gate['allControlsExercised']
    write(report/'acceptance.json',gate);write(report/'evidence-index.json',source_index)
    for locale in COPY:(report/('report-'+locale+'.md')).write_text(render(arms['core'],arms['pi'],locale),encoding='utf-8')
    return gate

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('settings',type=Path)
    args=parser.parse_args();print(json.dumps(finalize(read(args.settings))))
