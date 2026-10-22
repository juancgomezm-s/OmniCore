import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { validateView } from '../schema-validation.mjs';
const corpus = JSON.parse(readFileSync(new URL('./view-corpus.json', import.meta.url), 'utf8'));
for (const sample of corpus.cases) test('shared view corpus: ' + sample.id, () => {
  const value = { ...structuredClone(corpus.base), ...sample.changes };
  assert.equal(validateView(value), sample.valid, JSON.stringify(validateView.errors));
});
