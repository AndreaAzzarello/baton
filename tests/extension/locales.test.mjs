import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const messages = (lang) => JSON.parse(readFileSync(new URL(`../../extension/_locales/${lang}/messages.json`, import.meta.url), 'utf8'));

// C4: the popup section is not "Rules" (that word names the rules of the Baton app, 1.4.1): it says what it decides.
test('the popup section that decides what to intercept says so, not "Rules"', () => {
  assert.equal(messages('en').rulesTitle.message, 'When to intercept');
  assert.equal(messages('it').rulesTitle.message, 'Quando intercettare');
});

// The site menu's neutral choice points at the section by its (C4) name, so the two cannot drift apart.
test('the neutral choice of the site menu names the "When to intercept" section', () => {
  for (const lang of ['en', 'it']) {
    assert.ok(messages(lang).siteRuleNormal.message.includes(messages(lang).rulesTitle.message), lang);
  }
});

test('both languages have the same keys', () => {
  assert.deepEqual(Object.keys(messages('en')).sort(), Object.keys(messages('it')).sort());
});
