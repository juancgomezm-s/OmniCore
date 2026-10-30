"""Compare external harness receipts on one exact regression task set (spec §97).

This tool never launches agents, imports project code, invokes a provider or substitutes
fixtures for real model measurements. Unknown metrics stay null, including totals.
"""
import argparse
import json
import math
from pathlib import Path

SCHEMA = 'omnicore.regression-measurements.v1'
METRICS = ('timeSeconds', 'turns', 'toolCalls', 'tokens', 'contextGrowthTokens', 'frontierEscalations', 'costUsd')
COPY = {
    'es': ('Comparación de regresión', 'Métrica', 'Sin evidencia', 'Identidad de modelo sin verificar', 'Muestras sintéticas: no acreditan calidad del modelo'),
    'en': ('Regression comparison', 'Metric', 'No evidence', 'Model identity unverified', 'Synthetic samples do not establish model quality'),
    'fr': ('Comparaison de régression', 'Mesure', 'Sans preuve', 'Identité du modèle non vérifiée', 'Les échantillons synthétiques ne prouvent pas la qualité du modèle'),
    'it': ('Confronto di regressione', 'Metrica', 'Nessuna prova', 'Identità del modello non verificata', 'I campioni sintetici non dimostrano la qualità del modello'),
}

def validate(document):
    if document.get('schema') != SCHEMA:
        raise ValueError('measurement schema mismatch')
    identity = document.get('benchmarkIdentity', {})
    for key in ('suiteId', 'suiteVersion', 'taskSetHash'):
        if not isinstance(identity.get(key), str) or not identity[key]:
            raise ValueError('missing benchmark identity: ' + key)
    if document.get('runtime') not in ('omnicore-cli', 'omnicoder-pi'):
        raise ValueError('runtime must identify the actual harness')
    if document.get('measurementSource') not in ('live', 'fixture'):
        raise ValueError('measurementSource must be explicit')
    records = document.get('tasks')
    if not isinstance(records, list) or not records or len(records) > 10000:
        raise ValueError('task set outside bounds')
    ids = []
    for record in records:
        task = record.get('id')
        if not isinstance(task, str) or not task or task in ids:
            raise ValueError('missing or duplicate task id')
        ids.append(task)
        if record.get('success') is not None and type(record['success']) is not bool:
            raise ValueError('success must be a boolean or null')
        for metric in METRICS:
            value = record.get(metric)
            if value is not None and (type(value) not in (int, float) or not math.isfinite(value) or value < 0):
                raise ValueError('invalid metric: ' + metric)
            if value is not None and metric not in ('timeSeconds', 'costUsd') and int(value) != value:
                raise ValueError('count must be integral: ' + metric)
    return document

def compare(left, right):
    left, right = validate(left), validate(right)
    if left['runtime'] == right['runtime']:
        raise ValueError('comparison requires distinct harnesses')
    if left['benchmarkIdentity'] != right['benchmarkIdentity']:
        raise ValueError('benchmark or sampling identity mismatch')
    if {r['id'] for r in left['tasks']} != {r['id'] for r in right['tasks']}:
        raise ValueError('task coverage mismatch')
    def aggregate(document):
        records = document['tasks']
        def total(name):
            values = [record.get(name) for record in records]
            return sum(values) if all(value is not None for value in values) else None
        success = [record.get('success') for record in records]
        return {'runtime': document['runtime'], 'measurementSource': document['measurementSource'],
                'tasks': len(records), 'successRate': sum(success) / len(success) if all(v is not None for v in success) else None,
                **{metric: total(metric) for metric in METRICS}}
    model_verified = bool(left.get('modelIdentity')) and left.get('modelIdentity') == right.get('modelIdentity')
    live = left['measurementSource'] == right['measurementSource'] == 'live'
    return {'schema': 'omnicore.regression-comparison.v1', 'benchmarkIdentity': left['benchmarkIdentity'],
            'declaredModelIdentityMatches': model_verified, 'bothDeclaredLive': live,
            'comparableByDeclaredIdentity': model_verified and live,
            'provenanceVerified': False,
            'results': [aggregate(left), aggregate(right)]}

def render(report, locale='es'):
    title, metric, missing, model_warning, fixture_warning = COPY[locale]
    left, right = report['results']
    lines = ['# ' + title, '', '| ' + metric + ' | ' + left['runtime'] + ' | ' + right['runtime'] + ' |', '|---|---:|---:|']
    for name in ('successRate', *METRICS):
        def value(record):
            return missing if record[name] is None else format(record[name], '.6g')
        lines.append('| ' + name + ' | ' + value(left) + ' | ' + value(right) + ' |')
    if not report['declaredModelIdentityMatches']: lines.extend(['', model_warning])
    if not report['bothDeclaredLive']: lines.extend(['', fixture_warning])
    return '\n'.join(lines) + '\n'

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('omnicore', type=Path)
    parser.add_argument('pi', type=Path)
    parser.add_argument('--locale', choices=COPY, default='es')
    parser.add_argument('--json-output', type=Path)
    args = parser.parse_args()
    def read(path):
        if path.stat().st_size > 16 * 1024 * 1024: raise ValueError('receipt exceeds limit')
        return json.loads(path.read_text(encoding='utf8'))
    report = compare(read(args.omnicore), read(args.pi))
    if args.json_output:
        args.json_output.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n', encoding='utf8')
    print(render(report, args.locale), end='')

if __name__ == '__main__':
    main()
