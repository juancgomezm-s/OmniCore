import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('comparison', Path(__file__).parents[1] / 'tools/evaluation/compare.py')
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)

def receipt(runtime):
    return {'schema': comparison.SCHEMA, 'runtime': runtime, 'measurementSource': 'fixture',
            'benchmarkIdentity': {'suiteId': 'fixture', 'suiteVersion': '1', 'taskSetHash': 'same', 'seed': None, 'temperature': None},
            'modelIdentity': None, 'tasks': [{'id': 'a', 'success': True, 'timeSeconds': 1, 'turns': 1, 'toolCalls': 0, 'tokens': 5}]}

class RegressionComparisonTests(unittest.TestCase):
    def test_absent_cost_and_context_are_unknown_not_free_or_zero(self):
        result = comparison.compare(receipt('omnicore-cli'), receipt('omnicoder-pi'))
        self.assertEqual(1, result['results'][0]['successRate'])
        self.assertIsNone(result['results'][0]['costUsd'])
        self.assertIsNone(result['results'][0]['contextGrowthTokens'])
        self.assertFalse(result['comparableByDeclaredIdentity'])
        self.assertFalse(result['provenanceVerified'])
        for locale in comparison.COPY:
            self.assertIn(comparison.COPY[locale][2], comparison.render(result, locale))

    def test_task_identity_sampling_and_coverage_must_match(self):
        left, right = receipt('omnicore-cli'), receipt('omnicoder-pi')
        for change in ('taskSetHash', 'seed', 'temperature'):
            altered = copy.deepcopy(right); altered['benchmarkIdentity'][change] = 'different'
            with self.assertRaises(ValueError): comparison.compare(left, altered)
        right['tasks'][0]['id'] = 'different'
        with self.assertRaises(ValueError): comparison.compare(left, right)

    def test_duplicates_negative_nan_and_boolean_counts_are_rejected(self):
        for value in (-1, float('nan'), True):
            document = receipt('omnicore-cli'); document['tasks'][0]['tokens'] = value
            with self.assertRaises(ValueError): comparison.validate(document)
        document = receipt('omnicore-cli'); document['tasks'].append(document['tasks'][0])
        with self.assertRaises(ValueError): comparison.validate(document)

    def test_missing_outcome_does_not_count_as_failure_or_success(self):
        left, right = receipt('omnicore-cli'), receipt('omnicoder-pi')
        left['tasks'][0]['success'] = None
        self.assertIsNone(comparison.compare(left, right)['results'][0]['successRate'])

if __name__ == '__main__': unittest.main()
