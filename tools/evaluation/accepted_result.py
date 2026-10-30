"""Evaluate the native accepted implementation tree, without publishing it."""
from contextlib import closing
from dataclasses import replace
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess

from agentic_suite import oracle


def accepted_worktree(evidence, private_root):
    snapshot = evidence / 'journal-snapshot.db'
    with closing(sqlite3.connect(snapshot.as_uri() + '?mode=ro&immutable=1', uri=True)) as db:
        rows = [(kind, json.loads(payload)) for kind, payload in db.execute(
            "select event_type,payload from events where event_type in "
            "('result_disposition.recorded','lane.worktree_bound') order by seq")]
    accepted = {body['Disposition']['ExecutionId']['Value'] for kind, body in rows
                if kind == 'result_disposition.recorded' and body['Disposition']['Outcome'] == 0}
    bound = [body for kind, body in rows if kind == 'lane.worktree_bound'
             and body['ExecutionId']['Value'] in accepted
             and body['OwnerExecutionId']['Value'] in accepted]
    if len(accepted) < 3 or len(bound) != 1:
        raise ValueError('verified_implementation_binding_missing')
    path = Path(bound[0]['WorktreePath']).resolve()
    if not path.is_relative_to(Path(private_root).resolve()):
        raise ValueError('foreign_result_tree')
    common = subprocess.run(
        ['git', '-C', str(path), 'rev-parse', '--path-format=absolute', '--git-common-dir'],
        capture_output=True, check=True, timeout=20)
    if Path(common.stdout.decode('utf-8').strip()).resolve() != Path(bound[0]['GitCommonDirectory']).resolve():
        raise ValueError('worktree_repository_changed')
    return path, {
        'acceptedResults': len(accepted),
        'ownerExecutionId': bound[0]['OwnerExecutionId']['Value'],
        'verifyExecutionId': bound[0]['ExecutionId']['Value'],
        'sourceTree': bound[0]['SourceTree'],
        'resultLocation': 'verified-owned-implementation-tree',
        'parentWorkspacePublished': False,
    }


def result_oracle(case, workspace, node, output, evidence, private_root, delegated=False):
    """Use the same independent oracle during a run and offline finalization."""
    binding = None
    if delegated:
        workspace, binding = accepted_worktree(evidence, private_root)
        # The Host-created .git pointer is metadata, never an allowed model edit.
        # Its repository binding is checked above; pin its exact bytes separately.
        pointer = workspace / '.git'
        case = replace(case, files={**case.files, '.git': pointer.read_text(encoding='utf-8')})
        baseline = json.loads((output / 'baseline.json').read_text(encoding='utf-8'))
        baseline['.git'] = hashlib.sha256(pointer.read_bytes()).hexdigest()
        (output / 'baseline.json').write_text(json.dumps(baseline, indent=2) + '\n', encoding='utf-8')
    return oracle(case, workspace, node, output), binding
