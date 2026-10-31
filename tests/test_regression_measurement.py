import copy
from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
import uuid

ROOT = Path(__file__).parents[1]
def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result); return result
measure = module('measurement', ROOT / 'tools/evaluation/measure.py')
comparison = module('comparison', ROOT / 'tools/evaluation/compare.py')

def guid(): return str(uuid.uuid4())

class RegressionMeasurementTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='omni-measure-test-')
        self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        self.db = self.root / 'snapshot.db'; self.session = guid(); self.run = guid(); self.turn = guid(); self.lane = guid()
        self.rows = []
        self.add('run.created', {'RunId': {'Value': self.run}, 'SessionId': {'Value': self.session}})
        self.add('turn.started', {'TurnId': {'Value': self.turn}}, turn=self.turn)

    def add(self, kind, body=None, version=1, turn=None, lane=None, run=None, timestamp=None):
        seq = len(self.rows) + 1
        self.rows.append((self.session, seq, guid(), kind, version,
                          timestamp or '2026-10-30T12:00:%02d+00:00' % seq,
                          self.run if run is None else run, turn, lane, json.dumps(body or {})))
        return self.rows[-1][2]

    def step(self, step=0, incoming=100, outgoing=20, cost=0.02, fields=31, attempts=None, context=None):
        self.add('model_step.started', {'TurnId': {'Value': self.turn}, 'StepIndex': step,
                                       'ContextSnapshotRef': context}, version=4, turn=self.turn, lane=self.lane)
        self.add('model_step.completed', {'TurnId': {'Value': self.turn}, 'StepIndex': step,
                  'Usage': {'Input': incoming, 'Output': outgoing, 'CacheRead': 30, 'CacheWrite': 0, 'Reasoning': 5},
                  'ReportedUsageFields': fields, 'CostUsd': cost, 'CostIncomplete': False,
                  'GenerationAttempts': attempts or {'ObservedGenerationSends': 1, 'MaximumGenerationRequestAttempts': 1}},
                 version=5, turn=self.turn, lane=self.lane)

    def write(self, complete=True):
        if complete: self.add('run.completed', {'RunId': {'Value': self.run}, 'Outcome': 0})
        with closing(sqlite3.connect(self.db)) as db, db:
            db.execute('create table events(session_id text,seq integer,event_id text,event_type text,schema_version integer,timestamp text,run_id text,turn_id text,lane_id text,payload text)')
            db.executemany('insert into events values(?,?,?,?,?,?,?,?,?,?)', self.rows)
        self.document = {'schema': measure.SCHEMA, 'measurementSource': 'fixture',
            'benchmarkIdentity': {'suiteId': 'fixed-fixture', 'suiteVersion': '1.0.0', 'taskSetHash': 'a' * 64},
            'modelIdentity': None, 'tasks': [{'id': 'fix', 'success': True, 'oracleEvidenceSha256': 'b' * 64,
                'runs': [{'journalSnapshot': 'snapshot.db', 'sessionId': self.session, 'runId': self.run}]}]}
        return self.document

    def result(self): return measure.export(self.document, self.root)['tasks'][0]

    def blob(self, tokens, accuracy='exact', **overrides):
        body = {'contextScopeVersion': 1, 'sessionId': self.session, 'runId': self.run,
                'turnId': self.turn, 'laneId': self.lane, 'tokenCount': tokens,
                'tokenAccuracy': accuracy, 'items': [{'content': 'private text must not be exported'}], **overrides}
        raw = json.dumps(body).encode(); digest = hashlib.sha256(raw).hexdigest()
        path = self.root / 'blobs/sha256' / digest[:2] / digest[2:4] / digest
        path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(raw)
        return {'Hash': {'Algorithm': 'sha256', 'Value': digest}, 'Size': len(raw)}, path

    def test_counts_requested_tools_and_turns_once_without_double_counting_usage_subsets(self):
        self.step(); self.add('toolcall.requested', {'ToolCallId': {'Value': guid()}, 'ArgumentsJson': 'private-secret'})
        self.add('toolcall.prepared'); self.add('toolcall.started'); self.add('toolcall.succeeded')
        self.write(); before = self.db.read_bytes(); result = self.result()
        self.assertEqual(1, result['turns']); self.assertEqual(1, result['toolCalls'])
        self.assertEqual(120, result['tokens']); self.assertEqual(0.02, result['costUsd'])
        self.assertEqual(0, result['frontierEscalations']); self.assertIsNone(result['contextGrowthTokens'])
        self.assertEqual(before, self.db.read_bytes()); self.assertFalse(Path(str(self.db) + '-wal').exists())
        encoded = json.dumps(result); self.assertNotIn('private-secret', encoded)
        self.assertNotIn(str(self.db), encoded)

    def test_missing_reported_usage_and_cost_are_unknown_instead_of_zero(self):
        self.step(fields=1, cost=0); self.write(); result = self.result()
        self.assertIsNone(result['tokens']); self.assertIsNone(result['costUsd'])

    def test_unobserved_retry_usage_invalidates_total_even_if_latest_response_has_usage(self):
        self.step(attempts={'ObservedGenerationSends': 2, 'MaximumGenerationRequestAttempts': 3})
        self.write(); self.assertIsNone(self.result()['tokens']); self.assertIsNone(self.result()['costUsd'])

    def test_reported_zero_is_retained_when_usage_is_complete(self):
        self.step(cost=0); self.write(); self.assertEqual(0, self.result()['costUsd'])

    def test_meta_usage_is_included_but_cache_and_reasoning_are_not_added_again(self):
        self.step()
        self.add('meta_model.invocation_started', {'InvocationId': 'meta1'})
        self.add('meta_model.invocation_completed', {'InvocationId': 'meta1',
            'Usage': {'Input': 10, 'Output': 5, 'CacheRead': 0, 'CacheWrite': 0, 'Reasoning': 2},
            'ReportedUsageFields': 31, 'GenerationAttempts': {'ObservedGenerationSends': 1, 'MaximumGenerationRequestAttempts': 1},
            'CostUsd': 0.01}, version=3)
        self.write(); self.assertEqual(135, self.result()['tokens']); self.assertEqual(0.03, self.result()['costUsd'])

    def test_open_or_unresolved_invocation_is_not_partial_usage_presented_as_total(self):
        self.step()
        self.add('model_step.started', {'TurnId': {'Value': self.turn}, 'StepIndex': 1}, version=4)
        self.write(); self.assertIsNone(self.result()['tokens']); self.assertIsNone(self.result()['costUsd'])

    def test_not_dispatched_attempt_does_not_erase_complete_reported_usage(self):
        self.step()
        self.add('model_step.started', {'TurnId': {'Value': self.turn}, 'StepIndex': 1}, version=4)
        self.add('model_step.not_dispatched', {'TurnId': {'Value': self.turn}, 'StepIndex': 1})
        self.write(); self.assertEqual(120, self.result()['tokens'])

    def test_no_model_receipt_is_not_a_free_model_measurement(self):
        self.write(); self.assertIsNone(self.result()['tokens']); self.assertIsNone(self.result()['costUsd'])

    def test_pending_run_has_no_terminal_result_or_partial_metrics(self):
        self.step(); self.write(complete=False); result = self.result()
        for key in ('success', 'timeSeconds', 'tokens', 'turns', 'toolCalls'): self.assertIsNone(result[key])

    def test_foreign_run_payloads_are_not_read_counted_or_exported(self):
        self.step(); other = guid()
        self.add('toolcall.requested', {'ToolCallId': {'Value': guid()}, 'secret': 'other run'}, run=other)
        self.write(); self.assertEqual(0, self.result()['toolCalls']); self.assertNotIn('other run', json.dumps(self.result()))

    def test_gaps_and_duplicate_events_are_rejected(self):
        self.write()
        with closing(sqlite3.connect(self.db)) as db, db: db.execute('update events set seq=5 where seq=2')
        with self.assertRaises(ValueError): self.result()

    def test_duplicate_operation_id_is_rejected_not_silently_deduplicated(self):
        call = guid()
        self.add('toolcall.requested', {'ToolCallId': {'Value': call}})
        self.add('toolcall.requested', {'ToolCallId': {'Value': call}})
        self.write()
        with self.assertRaises(ValueError): self.result()

    def test_context_growth_uses_verified_scoped_exact_snapshots(self):
        first, _ = self.blob(100); second, _ = self.blob(160)
        self.step(context=first); self.step(step=1, context=second); self.write()
        self.document['tasks'][0]['runs'][0]['artifactRoot'] = '.'
        result = self.result(); self.assertEqual(60, result['contextGrowthTokens'])
        self.assertNotIn('private text', json.dumps(result))

    def test_estimated_context_remains_unknown_for_exact_comparison(self):
        ref, _ = self.blob(100, accuracy='estimated'); self.step(context=ref); self.write()
        self.document['tasks'][0]['runs'][0]['artifactRoot'] = '.'
        self.assertIsNone(self.result()['contextGrowthTokens'])

    def test_changed_context_blob_or_foreign_scope_is_rejected(self):
        ref, path = self.blob(100); self.step(context=ref); self.write()
        self.document['tasks'][0]['runs'][0]['artifactRoot'] = '.'
        path.write_bytes(b'changed')
        with self.assertRaises(ValueError): self.result()

    def test_frontier_is_not_inferred_from_model_name_or_every_escalation(self):
        self.add('model.escalation_completed', {'RunId': {'Value': self.run}, 'ToModel': 'arbitrary-model'}, version=2)
        self.write(); self.assertIsNone(self.result()['frontierEscalations'])
        self.document['tasks'][0]['runs'][0]['frontierPolicy'] = {'modelIds': ['different'], 'evidenceSha256': 'e' * 64}
        self.assertEqual(0, self.result()['frontierEscalations'])
        self.document['tasks'][0]['runs'][0]['frontierPolicy']['modelIds'] = ['arbitrary-model']
        self.assertEqual(1, self.result()['frontierEscalations'])

    def test_runtime_completion_is_not_task_success(self):
        self.write(); self.document['tasks'][0]['success'] = None
        self.assertIsNone(self.result()['success'])

    def test_legacy_model_completion_prevents_partial_token_total(self):
        self.step(); self.add('model.completed', {'TurnId': {'Value': guid()}})
        self.write(); self.assertIsNone(self.result()['tokens'])

    def test_backward_clock_is_unknown_instead_of_negative_time(self):
        self.add('run.completed', timestamp='2026-10-29T00:00:00+00:00')
        self.write(complete=False); self.assertIsNone(self.result()['timeSeconds'])

    def test_missing_context_blob_is_unknown_but_foreign_scope_is_rejected(self):
        ref, path = self.blob(100, runId=guid()); self.step(context=ref); self.write()
        self.document['tasks'][0]['runs'][0]['artifactRoot'] = '.'
        with self.assertRaises(ValueError): self.result()
        path.unlink(); self.assertIsNone(self.result()['contextGrowthTokens'])

    def test_future_model_schema_is_not_measured_under_old_semantics(self):
        self.step(); self.rows[-1] = (*self.rows[-1][:4], 6, *self.rows[-1][5:])
        self.write()
        with self.assertRaises(ValueError): self.result()

    def test_malformed_attestation_is_not_echoed_as_evidence(self):
        self.write(); self.document['tasks'][0]['success'] = None
        self.document['tasks'][0]['oracleEvidenceSha256'] = {'private': 'payload'}
        with self.assertRaises(ValueError): self.result()

    def test_duplicate_json_fields_are_rejected(self):
        with self.assertRaises(ValueError): measure.decode('{"success":true,"success":false}')
        with self.assertRaises(ValueError): measure.number(10 ** 500)

    def test_overlapping_runs_sum_counts_but_not_wall_clock_time(self):
        other_run, other_turn = guid(), guid()
        self.add('run.created', {'RunId': {'Value': other_run}}, run=other_run)
        self.add('turn.started', {'TurnId': {'Value': other_turn}}, run=other_run, turn=other_turn)
        self.add('run.completed', {'RunId': {'Value': other_run}}, run=other_run)
        self.write()
        self.document['tasks'][0]['runs'].append({'journalSnapshot': 'snapshot.db',
            'sessionId': self.session, 'runId': other_run})
        self.assertEqual(2, self.result()['turns']); self.assertIsNone(self.result()['timeSeconds'])

    def test_not_dispatched_before_its_start_cannot_establish_usage_coverage(self):
        self.step()
        self.add('model_step.not_dispatched', {'TurnId': {'Value': self.turn}, 'StepIndex': 1})
        self.add('model_step.started', {'TurnId': {'Value': self.turn}, 'StepIndex': 1}, version=4)
        self.write(); self.assertIsNone(self.result()['tokens'])

    def test_reusing_run_across_tasks_is_rejected(self):
        self.write(); second = copy.deepcopy(self.document['tasks'][0]); second['id'] = 'another'
        self.document['tasks'].append(second)
        with self.assertRaises(ValueError): self.result()

    def test_interrupted_snapshots_with_wal_are_refused(self):
        self.write(); Path(str(self.db) + '-wal').write_bytes(b'not a closed backup')
        with self.assertRaises(ValueError): self.result()

    def test_other_user_payload_cannot_be_smuggled_in_model_or_benchmark_identity(self):
        self.write(); self.document['benchmarkIdentity']['secret'] = 'private'
        with self.assertRaises(ValueError): self.result()
        self.document['benchmarkIdentity'].pop('secret')
        self.document['modelIdentity'] = {'endpoint': 'https://example.invalid/api?key=private'}
        with self.assertRaises(ValueError): self.result()

    def test_policy_mismatch_cannot_be_compared_as_equal_measurement(self):
        self.write(); left = measure.export(self.document, self.root); right = copy.deepcopy(left)
        right['runtime'] = 'omnicoder-pi'; right['measurementPolicy']['tools'] = 'executed-only'
        with self.assertRaises(ValueError): comparison.compare(left, right)

    def test_reports_are_localized_and_machine_metrics_are_unchanged(self):
        self.write(); result = measure.export(self.document, self.root)
        for locale in measure.COPY:
            text = measure.render(result, locale)
            self.assertIn(measure.COPY[locale][0], text); self.assertIn(measure.COPY[locale][3], text)
        self.assertEqual(1, result['tasks'][0]['turns'])

    def test_cli_does_not_overwrite_source_or_print_payload_on_error(self):
        self.write(); path = self.root / 'request.json'; path.write_text(json.dumps(self.document), encoding='utf-8')
        before = path.read_bytes()
        result = subprocess.run([sys.executable, str(ROOT/'tools/evaluation/measure.py'), str(path), '--json-output', str(path)],
                                capture_output=True, timeout=20)
        self.assertEqual(2, result.returncode); self.assertEqual(before, path.read_bytes())
        self.assertNotIn(b'Traceback', result.stdout + result.stderr)

if __name__ == '__main__': unittest.main()
