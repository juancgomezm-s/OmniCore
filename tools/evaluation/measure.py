"""Read an explicitly supplied, closed SQLite journal snapshot for regression metrics.

Offline audit utility, not a runtime client: no commands, providers, Session opening,
recovery, schema migrations or writes to the source. Protocol/UI JSON is deliberately
not accepted: it is a filtered projection and omits facts needed for measurement.
"""
import argparse
from collections import defaultdict
from contextlib import closing
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import re
import sqlite3
import uuid
from urllib.parse import urlsplit

SCHEMA = 'omnicore.regression-snapshot-input.v1'
POLICY = {
    'version': 1, 'time': 'created-to-terminal-journal-clock-including-waits',
    'turns': 'distinct-started-in-explicit-runs',
    'tools': 'all-requested-including-rejected-and-owned-children',
    'tokens': 'reported-input-plus-output-main-and-meta-with-complete-attempt-coverage',
    'context': 'max-per-lane-peak-minus-initial-exact-snapshot-tokens',
    'frontier': 'completed-escalations-under-explicit-harness-policy',
    'success': 'external-oracle-attestation-not-runtime-completion',
}
COPY = {
    'es': ('Medición de regresión', 'Métrica', 'Valor', 'Sin evidencia', 'Datos de evaluación no válidos', 'Procedencia no autenticada; éxito y modelo declarados por el arnés.'),
    'en': ('Regression measurement', 'Metric', 'Value', 'No evidence', 'Invalid evaluation data', 'Provenance is not authenticated; success and model are harness declarations.'),
    'fr': ('Mesure de régression', 'Mesure', 'Valeur', 'Sans preuve', 'Données d’évaluation invalides', 'Provenance non authentifiée ; réussite et modèle déclarés par le harnais.'),
    'it': ('Misurazione di regressione', 'Metrica', 'Valore', 'Nessuna prova', 'Dati di valutazione non validi', 'Provenienza non autenticata; esito e modello dichiarati dal sistema di prova.'),
}
LABELS = {
    'success': ('Éxito', 'Success', 'Réussite', 'Esito'),
    'timeSeconds': ('Tiempo (s)', 'Time (s)', 'Durée (s)', 'Tempo (s)'),
    'turns': ('Turns iniciados', 'Turns started', 'Tours démarrés', 'Turni avviati'),
    'toolCalls': ('Llamadas solicitadas', 'Requested tool calls', 'Appels demandés', 'Chiamate richieste'),
    'tokens': ('Tokens informados', 'Reported tokens', 'Tokens déclarés', 'Token riportati'),
    'contextGrowthTokens': ('Crecimiento de contexto exacto', 'Exact context growth', 'Croissance exacte du contexte', 'Crescita esatta del contesto'),
    'frontierEscalations': ('Escaladas frontier completadas', 'Completed frontier escalations', 'Escalades frontier terminées', 'Escalation frontier completate'),
    'costUsd': ('Costo informado (USD)', 'Reported cost (USD)', 'Coût déclaré (USD)', 'Costo riportato (USD)'),
}
MAX_EVENTS = 100000
MAX_PAYLOAD = 1024 * 1024
HEX = re.compile(r'[a-f0-9]{64}\Z')

def fail(code): raise ValueError(code)

def is_digest(value):
    return isinstance(value, str) and HEX.fullmatch(value) is not None

def number(value, integral=False):
    if (type(value) not in (int, float) or value < 0 or value > 2**53 - 1
            or type(value) is float and not math.isfinite(value)):
        fail('invalid_number')
    if integral and int(value) != value: fail('invalid_count')
    return int(value) if integral else value

def identifier(value):
    if isinstance(value, dict): value = value.get('Value')
    if not isinstance(value, str): fail('invalid_id')
    try: return str(uuid.UUID(value))
    except (ValueError, AttributeError): fail('invalid_id')

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result: fail('duplicate_json_key')
        result[key] = value
    return result

def decode(text):
    return json.loads(text, object_pairs_hook=unique_object,
                      parse_constant=lambda _: fail('nonfinite_json'))

def safe_file(path, maximum):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        if item.is_symlink() or getattr(item, 'is_junction', lambda: False)(): fail('linked_input')
        if item.exists() and getattr(item.lstat(), 'st_reparse_tag', 0): fail('linked_input')
    if not path.is_file() or path.stat().st_size > maximum: fail('input_size')
    return path

def file_hash(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def read_json(path, maximum=MAX_PAYLOAD):
    path = safe_file(path, maximum)
    return decode(path.read_text(encoding='utf-8-sig'))

def snapshot_rows(path, session, run):
    path = safe_file(path, 256 * 1024 * 1024)
    # immutable=1 prevents journal/SHM creation. Refusing WAL avoids silently losing
    # its committed suffix. A stable hash detects changes to the supplied copy.
    if any(Path(str(path) + suffix).exists() for suffix in ('-wal', '-shm', '-journal')):
        fail('closed_snapshot_required')
    before = file_hash(path)
    with closing(sqlite3.connect(path.as_uri() + '?mode=ro&immutable=1', uri=True)) as db:
        db.execute('PRAGMA query_only=ON'); db.execute('PRAGMA trusted_schema=OFF')
        db.setlimit(sqlite3.SQLITE_LIMIT_LENGTH, MAX_PAYLOAD + 4096)
        kind = db.execute("select type from sqlite_master where name='events'").fetchone()
        if kind != ('table',): fail('journal_schema')
        ids = set(); count = 0
        for sequence, event_id in db.execute('select seq,event_id from events where session_id=? order by seq limit ?', (session, MAX_EVENTS + 1)):
            count += 1
            if count > MAX_EVENTS or sequence != count: fail('incomplete_session_sequence')
            event_id = identifier(event_id)
            if event_id in ids: fail('duplicate_event')
            ids.add(event_id)
        if not count: fail('session_missing')
        rows = []; size = 0
        query = 'select seq,event_id,event_type,schema_version,timestamp,turn_id,lane_id,payload from events where session_id=? and run_id=? order by seq'
        for row in db.execute(query, (session, run)):
            if not isinstance(row[7], str) or not isinstance(row[2], str): fail('event_payload')
            size += len(row[7].encode('utf-8'))
            if size > 32 * 1024 * 1024: fail('payload_budget')
            body = decode(row[7])
            if not isinstance(body, dict): fail('event_payload')
            if body.get('RunId') is not None and identifier(body['RunId']) != run: fail('run_mismatch')
            if body.get('SessionId') is not None and identifier(body['SessionId']) != session: fail('session_mismatch')
            if body.get('TurnId') is not None and row[5] is not None and identifier(body['TurnId']) != identifier(row[5]):
                fail('turn_mismatch')
            if body.get('LaneId') is not None and row[6] is not None and identifier(body['LaneId']) != identifier(row[6]):
                fail('lane_mismatch')
            rows.append({'seq': row[0], 'id': identifier(row[1]), 'type': row[2], 'schema': row[3],
                         'time': row[4], 'turn': row[5], 'lane': row[6], 'body': body})
    if before != file_hash(path): fail('snapshot_changed')
    if not rows: fail('run_missing')
    return rows, before

def invocation_metrics(events):
    starts = {}; ends = {}; no_dispatch = {}; legacy = False
    for event in events:
        kind = event['type']; body = event['body']
        if kind.startswith('model_step.'):
            key = ('main', identifier(body.get('TurnId')), number(body.get('StepIndex'), True))
            terminal = kind == 'model_step.completed'; skipped = kind == 'model_step.not_dispatched'
            if kind not in ('model_step.started', 'model_step.completed', 'model_step.not_dispatched'):
                legacy = True; continue
        elif kind.startswith('meta_model.invocation_'):
            key = ('meta', body.get('InvocationId'))
            if not isinstance(key[1], str) or not key[1] or len(key[1]) > 256: fail('invocation_id')
            terminal = kind in ('meta_model.invocation_completed', 'meta_model.invocation_failed')
            skipped = kind == 'meta_model.invocation_not_dispatched'
            if kind not in ('meta_model.invocation_started', 'meta_model.invocation_completed',
                            'meta_model.invocation_failed', 'meta_model.invocation_not_dispatched'):
                legacy = True; continue
        else:
            continue
        maximum_schema = (5 if terminal else 1 if skipped else 4) if key[0] == 'main' else (3 if terminal else 1)
        if type(event['schema']) is not int or not 1 <= event['schema'] <= maximum_schema: fail('event_schema')
        if terminal:
            if key in ends or key in no_dispatch: fail('duplicate_invocation_terminal')
            ends[key] = event
        elif skipped:
            if key in no_dispatch or key in ends: fail('duplicate_invocation_terminal')
            no_dispatch[key] = event
        else:
            if key in starts: fail('duplicate_invocation_start')
            starts[key] = event
    for event in events:
        if event['type'] == 'model.completed' and not any(key[0] == 'main' and key[1] == identifier(event['body'].get('TurnId')) for key in starts): legacy = True
    if any(key not in starts or starts[key]['seq'] >= event['seq'] for key,event in (ends | no_dispatch).items()): legacy = True
    complete = bool(starts) and not legacy and set(starts) == set(ends) | set(no_dispatch)
    tokens = 0; cost = 0; tokens_known = complete and bool(ends); cost_known = tokens_known
    for key, event in ends.items():
        body = event['body']; fields = body.get('ReportedUsageFields'); usage = body.get('Usage')
        attempt = body.get('GenerationAttempts')
        coverage = False
        if isinstance(attempt, dict):
            sends = number(attempt.get('ObservedGenerationSends'), True)
            bound = attempt.get('MaximumGenerationRequestAttempts')
            if bound is not None:
                bound = number(bound, True)
                if bound < 1 or sends > bound: fail('attempt_bound')
                coverage = sends == 1 or sends == 0 and bound == 1
        valid_usage = (type(fields) is int and 0 <= fields <= 31 and fields & 3 == 3
                       and isinstance(usage, dict) and coverage)
        if valid_usage:
            incoming, outgoing = number(usage.get('Input'), True), number(usage.get('Output'), True)
            for flag, name, total in ((4, 'CacheRead', incoming), (8, 'CacheWrite', incoming), (16, 'Reasoning', outgoing)):
                if fields & flag and number(usage.get(name), True) > total: fail('usage_subset')
            tokens += incoming + outgoing
        else: tokens_known = False
        value = body.get('CostUsd')
        minimum_schema = 5 if key[0] == 'main' else 3
        if (value is None or body.get('CostIncomplete', False) or not valid_usage
                or event['schema'] < minimum_schema): cost_known = False
        else: cost += number(value)
    return {'tokens': tokens if tokens_known else None, 'costUsd': cost if cost_known else None,
            'modelStepsObserved': len([key for key in starts if key[0] == 'main']),
            'metaInvocationsObserved': len([key for key in starts if key[0] == 'meta']),
            'usageCoverageComplete': complete and tokens_known}

def context_metrics(events, root, session, run):
    starts = [event for event in events if event['type'] == 'model_step.started']
    if not starts or root is None: return None
    groups = defaultdict(list)
    for event in starts:
        ref = event['body'].get('ContextSnapshotRef')
        if not isinstance(ref, dict) or not event['lane']: return None
        content = ref.get('Hash', {})
        if not isinstance(content, dict): fail('context_hash')
        digest = content.get('Value', '')
        if content.get('Algorithm') != 'sha256' or not is_digest(digest): fail('context_hash')
        candidate = Path(root) / 'blobs/sha256' / digest[:2] / digest[2:4] / digest
        if not candidate.exists() and not candidate.is_symlink(): return None
        path = safe_file(candidate, 16 * MAX_PAYLOAD)
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != digest or type(ref.get('Size')) is not int or len(raw) != ref['Size']: fail('context_integrity')
        body = decode(raw.decode('utf-8-sig'))
        if not isinstance(body, dict): fail('context_scope')
        if (body.get('contextScopeVersion') != 1 or identifier(body.get('sessionId')) != session
                or identifier(body.get('runId')) != run or identifier(body.get('turnId')) != identifier(event['body']['TurnId'])
                or identifier(body.get('laneId')) != identifier(event['lane'])): fail('context_scope')
        if body.get('tokenAccuracy') != 'exact': return None
        groups[event['lane']].append(number(body.get('tokenCount'), True))
    return max(max(values) - values[0] for values in groups.values())

def measure_run(spec, base):
    session, run = identifier(spec.get('sessionId')), identifier(spec.get('runId'))
    rows, digest = snapshot_rows(base / spec['journalSnapshot'], session, run)
    created = [row for row in rows if row['type'] == 'run.created']
    terminals = [row for row in rows if row['type'] in ('run.completed', 'run.failed', 'run.cancelled')]
    if len(created) != 1 or len(terminals) > 1: fail('run_lifecycle')
    if not terminals:
        return {'complete': False, 'sourceSha256': digest, 'runId': run, 'sessionId': session}
    terminal = terminals[0]
    if terminal['seq'] <= created[0]['seq']: fail('run_lifecycle')
    events = [row for row in rows if created[0]['seq'] <= row['seq'] <= terminal['seq']]
    elapsed = None
    try:
        first, last = (datetime.fromisoformat(row['time']) for row in (created[0], terminal))
        if first.tzinfo and last.tzinfo and last >= first: elapsed = (last - first).total_seconds()
    except (ValueError, TypeError): pass
    turns = [identifier(event['body'].get('TurnId')) for event in events if event['type'] == 'turn.started']
    calls = [identifier(event['body'].get('ToolCallId')) for event in events if event['type'] == 'toolcall.requested']
    if len(turns) != len(set(turns)) or len(calls) != len(set(calls)): fail('duplicate_operation')
    escalations = [event for event in events if event['type'] == 'model.escalation_completed']
    classification = spec.get('frontierPolicy')
    frontier = 0 if not escalations else None
    if classification is not None:
        if not isinstance(classification, dict): fail('frontier_attestation')
        targets = classification.get('modelIds')
        if (not isinstance(targets, list) or any(not isinstance(value, str) or not value for value in targets)
                or not is_digest(classification.get('evidenceSha256'))): fail('frontier_attestation')
        frontier = sum(event['body'].get('ToModel') in targets for event in escalations)
    artifact_root = base / spec['artifactRoot'] if spec.get('artifactRoot') else None
    return {'complete': True, 'sourceSha256': digest, 'sessionId': session, 'runId': run,
            'throughSequence': terminal['seq'], 'runtimeOutcome': terminal['type'],
            'timeSeconds': elapsed, 'turns': len(turns), 'toolCalls': len(calls),
            'frontierEscalations': frontier, 'modelEscalationsObserved': len(escalations),
            'frontierClassification': 'harness-declared-policy' if classification is not None else 'unavailable',
            'contextGrowthTokens': context_metrics(events, artifact_root, session, run),
            **invocation_metrics(events)}

def export(document, base=Path('.')):
    if not isinstance(document, dict): fail('input_schema')
    if document.get('schema') != SCHEMA: fail('input_schema')
    source = document.get('measurementSource')
    if source not in ('live', 'fixture'): fail('measurement_source')
    identity = document.get('benchmarkIdentity', {})
    if not isinstance(identity, dict) or set(identity) - {'suiteId','suiteVersion','taskSetHash','seed','temperature','samplingParametersSent'}: fail('benchmark_identity')
    if identity.get('seed') is not None: number(identity['seed'],True)
    if identity.get('temperature') is not None: number(identity['temperature'])
    if identity.get('samplingParametersSent') is not None and type(identity['samplingParametersSent']) is not bool: fail('sampling_identity')
    if (any(not isinstance(identity.get(key), str) or not identity[key] for key in ('suiteId', 'suiteVersion'))
            or not is_digest(identity.get('taskSetHash'))): fail('benchmark_identity')
    model = document.get('modelIdentity')
    if model is not None:
        allowed = {'providerId', 'modelId', 'modelRevision', 'adapterProfile', 'endpoint', 'protocol'}
        if not isinstance(model, dict) or set(model) - allowed: fail('model_identity')
        for value in model.values():
            if value is not None and (not isinstance(value, str) or len(value) > 512 or any(ord(c) < 32 for c in value)):
                fail('model_identity')
        if model.get('endpoint'):
            url = urlsplit(model['endpoint'])
            if url.scheme not in ('https', 'http') or not url.hostname or url.username or url.password or url.query or url.fragment:
                fail('endpoint_credentials')
    tasks = document.get('tasks')
    if not isinstance(tasks, list) or not 1 <= len(tasks) <= 128: fail('task_bounds')
    output = []; task_ids = set(); run_ids = set()
    metrics = ('timeSeconds', 'turns', 'toolCalls', 'tokens', 'contextGrowthTokens', 'frontierEscalations', 'costUsd')
    for task in tasks:
        if not isinstance(task, dict): fail('task_schema')
        task_id = task.get('id')
        if not isinstance(task_id, str) or not re.fullmatch(r'[A-Za-z0-9._-]{1,128}', task_id) or task_id in task_ids:
            fail('task_id')
        task_ids.add(task_id); runs = task.get('runs')
        if not isinstance(runs, list) or not 1 <= len(runs) <= 32: fail('run_bounds')
        results = []
        for spec in runs:
            if not isinstance(spec, dict): fail('run_schema')
            key = (identifier(spec.get('sessionId')), identifier(spec.get('runId')))
            if key in run_ids: fail('reused_run')
            run_ids.add(key); results.append(measure_run(spec, Path(base)))
        complete = all(run['complete'] for run in results)
        success = task.get('success')
        oracle = task.get('oracleEvidenceSha256')
        if oracle is not None and not is_digest(oracle): fail('oracle_attestation')
        if success is not None:
            if type(success) is not bool or not is_digest(oracle): fail('oracle_attestation')
        values = {}
        for metric in metrics:
            known = complete and all(run.get(metric) is not None for run in results)
            if metric == 'timeSeconds' and len(results) > 1: known = False # Overlapping runs must not be summed as wall time.
            if metric == 'contextGrowthTokens': values[metric] = max(run[metric] for run in results) if known else None
            else: values[metric] = sum(run[metric] for run in results) if known else None
            if values[metric] is not None: number(values[metric], metric not in ('timeSeconds','costUsd'))
        output.append({'id': task_id, 'success': success if complete else None, **values,
                       'successSource': 'harness-oracle-attestation' if success is not None else 'unavailable',
                       'oracleEvidenceSha256': task.get('oracleEvidenceSha256'), 'runs': results})
    return {'schema': 'omnicore.regression-measurements.v1', 'runtime': 'omnicore-cli',
            'measurementSource': source, 'benchmarkIdentity': identity, 'modelIdentity': model,
            'measurementPolicy': dict(POLICY), 'provenanceVerified': False, 'tasks': output}

def render(document, locale):
    title, metric, value, missing, _, note = COPY[locale]; index = tuple(COPY).index(locale)
    lines = ['# ' + title, '', note]
    for task in document['tasks']:
        lines += ['', '## ' + task['id'], '', f'| {metric} | {value} |', '|---|---:|']
        for key, names in LABELS.items():
            number = task[key]
            shown = missing if number is None else ('1' if number is True else '0' if number is False else format(number, '.9g'))
            lines.append('| ' + names[index] + ' | ' + shown + ' |')
    return '\n'.join(lines) + '\n'

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path); parser.add_argument('--json-output', required=True, type=Path)
    parser.add_argument('--locale', choices=COPY, default='es'); args = parser.parse_args()
    try:
        result = export(read_json(args.input), args.input.absolute().parent)
        if args.json_output.exists(): fail('output_exists')
        with args.json_output.open('x', encoding='utf-8') as stream:
            json.dump(result, stream, indent=2, ensure_ascii=False, allow_nan=False); stream.write('\n')
        print(render(result, args.locale), end='')
        return 0
    except (ValueError, KeyError, TypeError, OSError, sqlite3.Error):
        # Do not print provider responses, SQL payloads, paths or exception text.
        print(COPY[args.locale][4]); return 2

if __name__ == '__main__': raise SystemExit(main())
