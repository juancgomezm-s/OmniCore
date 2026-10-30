import hashlib
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('agentic_suite',Path(__file__).parents[1]/'tools/evaluation/agentic_suite.py')
import sys
sys.path.insert(0,str(Path(__file__).parents[1]/'tools/evaluation'))
from accepted_result import accepted_worktree, result_oracle
suite=importlib.util.module_from_spec(spec);sys.modules[spec.name]=suite;spec.loader.exec_module(suite)
spec_pty=importlib.util.spec_from_file_location('core_pty',Path(__file__).parents[1]/'tools/evaluation/core_pty.py')
pty=importlib.util.module_from_spec(spec_pty);spec_pty.loader.exec_module(pty)

class AcceptanceDriverTests(unittest.TestCase):
    def test_scoped_permission_choice_does_not_depend_on_its_numeric_position(self):
        for position in (1,2,3):
            prompt=f'Permission required\n[{position}] Allow once\nChoose an option (default: deny): '
            self.assertEqual('allow_once',pty.answer_for_prompt(prompt))

    def test_budget_and_unrecognized_prompts_are_not_approved(self):
        self.assertIsNone(pty.answer_for_prompt('Incomplete prompt'))
        self.assertEqual('deny',pty.answer_for_prompt('Budget exceeded\n[1] Continue\nChoose an option (default: deny): '))
        self.assertEqual('consent_once',pty.answer_for_prompt('Strong sandbox unavailable\n[2] Allow once\nChoose an option (default: deny): '))

@unittest.skipUnless(shutil.which('node'),'Node is required for the independent JavaScript oracles')
class AgenticSuiteTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='omni-agentic-oracle-');self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.node=shutil.which('node')

    def prepare(self,case):
        workspace=self.root/case.id;baseline=suite.prepare(case,workspace)
        evidence=self.root/(case.id+'-evidence');evidence.mkdir()
        (evidence/'baseline.json').write_text(json.dumps(baseline),encoding='utf-8')
        return workspace,evidence

    def test_exactly_the_eleven_fixed_categories_and_stable_hash(self):
        self.assertEqual(11,len(suite.CASES));self.assertEqual(11,len({case.category for case in suite.CASES}))
        self.assertEqual(11,len({case.id for case in suite.CASES}));self.assertEqual(64,len(suite.task_set_hash()))
        self.assertEqual(suite.task_set_hash(),suite.task_set_hash())

    def test_no_unmodified_bug_fixture_is_presented_as_solved(self):
        for case in suite.CASES:
            with self.subTest(case=case.id):
                workspace,evidence=self.prepare(case)
                result=suite.oracle(case,workspace,self.node,evidence)
                self.assertEqual(case.control=='deny-write',result['behavior'])
                self.assertTrue(result['preservation'])

    def test_behavior_pass_does_not_hide_changes_to_protected_tests(self):
        case=suite.CASES[1];workspace,evidence=self.prepare(case)
        (workspace/'src/math.mjs').write_text('export function add(a,b){return a+b;}\n',encoding='utf-8')
        result=suite.oracle(case,workspace,self.node,evidence);self.assertTrue(result['success'])
        (workspace/'tests/agentic-check.mjs').write_text('// bypassed tests\n',encoding='utf-8')
        result=suite.oracle(case,workspace,self.node,evidence)
        self.assertTrue(result['behavior']);self.assertFalse(result['success'])

    def test_preservation_compares_actual_baseline_bytes_including_windows_newlines(self):
        case=suite.CASES[8];workspace,evidence=self.prepare(case)
        protected=workspace/'protected.txt';protected.write_bytes(b'KEEP THIS CONTENT\r\n')
        baseline=json.loads((evidence/'baseline.json').read_text(encoding='utf-8'))
        baseline['protected.txt']=hashlib.sha256(protected.read_bytes()).hexdigest()
        (evidence/'baseline.json').write_text(json.dumps(baseline),encoding='utf-8')
        # This case's textual oracle intentionally distinguishes contents; preservation
        # independently compares exact original bytes rather than LF source templates.
        self.assertTrue(suite.oracle(case,workspace,self.node,evidence)['preservation'])

    def test_unexpected_files_and_deleted_originals_fail_even_after_correct_fix(self):
        case=suite.CASES[1];workspace,evidence=self.prepare(case)
        (workspace/'src/math.mjs').write_text('export function add(a,b){return a+b;}\n',encoding='utf-8')
        (workspace/'extra.txt').write_text('unrequested',encoding='utf-8')
        self.assertFalse(suite.oracle(case,workspace,self.node,evidence)['preservation'])
        (workspace/'extra.txt').unlink();(workspace/'tests/agentic-check.mjs').unlink()
        self.assertFalse(suite.oracle(case,workspace,self.node,evidence)['preservation'])

@unittest.skipUnless(shutil.which('git') and shutil.which('node'),'Git and Node are required for native result binding checks')
class AcceptedResultTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='omni-accepted-result-');self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name).resolve();self.evidence=self.root/'evidence';self.evidence.mkdir()
        self.case=next(c for c in suite.CASES if c.control=='delegation')
        self.parent=self.root/'parent';self.baseline=suite.prepare(self.case,self.parent)
        self.tree=self.root/'owned';suite.prepare(self.case,self.tree)
        self.git=self.root/'git';self.git.mkdir()
        subprocess.run(['git','init','--bare',str(self.git)],capture_output=True,check=True)
        (self.tree/'.git').write_text('gitdir: '+str(self.git)+'\n',encoding='utf-8')
        # Worktree content is separate from the unchanged parent. The journal is a
        # synthetic negative-test fixture, never live acceptance evidence.
        (self.tree/'src/math.mjs').write_text('export function add(a,b){return a+b;}\n',encoding='utf-8')
        self.bound={'ExecutionId':{'Value':'verify'},'OwnerExecutionId':{'Value':'implement'},
                    'WorktreePath':str(self.tree),'GitCommonDirectory':str(self.git),'SourceTree':'fixture-tree'}
        self.write_journal(['explore','implement','verify'])

    def write_journal(self,accepted):
        snapshot=self.evidence/'journal-snapshot.db'
        if snapshot.exists():snapshot.unlink()
        with closing(sqlite3.connect(snapshot)) as db:
            db.execute('create table events(seq integer,event_type text,payload text)')
            rows=[('result_disposition.recorded',{'Disposition':{'ExecutionId':{'Value':value},'Outcome':0}}) for value in accepted]
            rows.append(('lane.worktree_bound',self.bound))
            db.executemany('insert into events values(?,?,?)',[(i,k,json.dumps(b)) for i,(k,b) in enumerate(rows,1)])
            db.commit()

    def test_oracle_checks_verified_tree_and_preserves_original_baseline(self):
        original=self.evidence/'baseline.json';original.write_text(json.dumps(self.baseline),encoding='utf-8')
        before=original.read_bytes();output=self.root/'oracle';output.mkdir()
        (output/'baseline.json').write_bytes(before)
        checks,binding=result_oracle(self.case,self.parent,shutil.which('node'),output,self.evidence,self.root,delegated=True)
        self.assertTrue(checks['success']);self.assertFalse(binding['parentWorkspacePublished'])
        self.assertIn('a-b',(self.parent/'src/math.mjs').read_text(encoding='utf-8'))
        self.assertEqual(before,original.read_bytes())

    def test_missing_verify_acceptance_is_not_success(self):
        self.write_journal(['explore','implement','other'])
        with self.assertRaisesRegex(ValueError,'verified_implementation_binding_missing'):
            accepted_worktree(self.evidence,self.root)

    def test_unrelated_acceptances_do_not_bind_an_implementation(self):
        self.write_journal(['one','two','three'])
        with self.assertRaisesRegex(ValueError,'verified_implementation_binding_missing'):
            accepted_worktree(self.evidence,self.root)

    def test_foreign_tree_is_rejected_before_git_or_oracle(self):
        self.bound['WorktreePath']=str(self.root.parent/'foreign-tree');self.write_journal(['explore','implement','verify'])
        with self.assertRaisesRegex(ValueError,'foreign_result_tree'):
            accepted_worktree(self.evidence,self.root)

    def test_repository_change_invalidates_a_verified_binding(self):
        self.bound['GitCommonDirectory']=str(self.root/'foreign-git');self.write_journal(['explore','implement','verify'])
        with self.assertRaisesRegex(ValueError,'worktree_repository_changed'):
            accepted_worktree(self.evidence,self.root)

if __name__=='__main__':unittest.main()
