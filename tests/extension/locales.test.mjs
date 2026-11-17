import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const messages = (lang) => JSON.parse(readFileSync(new URL(`../../extension/_locales/${lang}/messages.json`, import.meta.url), 'utf8'));

// C4: the popup section is not "Rules" (that word names the rules of the Baton app, 1.4.1): it says what it decides.
test('the popup section that decides what to intercept says so, not "Rules"', () => {
  assert.equal(messages('en').rulesTitle.message, 'When to intercept');
  assert.equal(messages('it').rulesTitle.message, 'Quando intercettare');
});

test('both languages have the same keys', () => {
  assert.deepEqual(Object.keys(messages('en')).sort(), Object.keys(messages('it')).sort());
});
