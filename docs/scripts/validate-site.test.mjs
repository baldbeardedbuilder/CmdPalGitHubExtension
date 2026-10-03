import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { validateSite } from './validate-site.mjs';

async function fixture(t, files) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'cmdpal-docs-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  for (const [file, content] of Object.entries(files)) {
    const target = path.join(directory, file);
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, content);
  }
  return directory;
}

const searchAssets = {
  'pagefind/pagefind.js': '',
  'pagefind/pagefind-entry.json': '{}',
  'pagefind/en.pf_index': '',
};

test('accepts base-prefixed, relative, encoded, and same-origin links and assets', async (t) => {
  const directory = await fixture(t, {
    ...searchAssets,
    'index.html': `<a href="/CmdPalGitHubExtension/guide/#read-me">Guide</a>
      <a href="https://baldbeardedbuilder.github.io/CmdPalGitHubExtension/guide/?a=1&amp;b=2#read-me">Absolute</a>
      <a href="guide/">Relative</a><img src="/CmdPalGitHubExtension/image.svg">
      <link href="/CmdPalGitHubExtension/style.css" rel="stylesheet">
      <a href="https://github.com/somewhere">External</a><a href="mailto:hello@example.com">Email</a>`,
    'guide/index.html': '<h2 id="read-me">Read me</h2><a href="#read%2Dme">Anchor</a><a href="../">Home</a>',
    'image.svg': '<svg></svg>',
    'style.css': 'body { background-image: url("./image.svg"); }',
  });
  const result = await validateSite(directory);
  assert.deepEqual(result.errors, []);
  assert.equal(result.htmlCount, 2);
  assert.equal(result.referenceCount, 10);
});

test('rejects links and resources that escape the project prefix', async (t) => {
  const directory = await fixture(t, {
    ...searchAssets,
    'index.html': '<a href="/guide/">Guide</a><img src="/image.svg"><script src="/script.js"></script>',
    'guide/index.html': '',
    'image.svg': '',
    'script.js': '',
  });
  const result = await validateSite(directory);
  assert.equal(result.errors.length, 3);
  assert.ok(result.errors.every((error) => error.includes('outside the Pages base path')));
});

test('rejects missing pages, case mismatches, anchors, and responsive image assets', async (t) => {
  const directory = await fixture(t, {
    ...searchAssets,
    'index.html': `<a href="missing/">Missing page</a><a href="guide/#missing">Missing anchor</a>
      <a href="Guide/">Wrong case</a><img srcset="image.svg 1x, missing.svg 2x">`,
    'guide/index.html': '<h2 id="present">Present</h2>',
    'image.svg': '',
  });
  const result = await validateSite(directory);
  assert.equal(result.errors.length, 4);
  assert.ok(result.errors.some((error) => error.includes('missing anchor')));
  assert.ok(result.errors.some((error) => error.includes('missing.svg')));
});

test('checks CSS resources but ignores data URLs and inline script text', async (t) => {
  const directory = await fixture(t, {
    ...searchAssets,
    'index.html': `<script>const example = '<a href="/not-a-link/">';</script>
      <img src="data:image/svg+xml;base64,PHN2Zz4=">`,
    'style.css': 'a { background: url("missing.png"); } b { background: url(data:image/png;base64,AA==); }',
  });
  const result = await validateSite(directory);
  assert.equal(result.errors.length, 1);
  assert.ok(result.errors[0].includes('missing.png'));
});

test('requires generated pages and Pagefind artifacts', async (t) => {
  const directory = await fixture(t, {});
  const result = await validateSite(directory);
  assert.deepEqual(result.errors, [
    'No generated HTML pages found.',
    'Missing search asset: pagefind/pagefind.js',
    'Missing search asset: pagefind/pagefind-entry.json',
    'Missing Pagefind search index.',
  ]);
});

test('allows the generated 404 canonical but still checks its navigation and assets', async (t) => {
  const directory = await fixture(t, {
    ...searchAssets,
    'index.html': '<link rel="canonical" href="/wrong-base/">',
    '404.html': `<link rel="canonical" href="/CmdPalGitHubExtension/404/">
      <a href="/CmdPalGitHubExtension/">Home</a><img src="/CmdPalGitHubExtension/missing.svg">`,
  });
  const result = await validateSite(directory);
  assert.equal(result.errors.length, 2);
  assert.ok(result.errors.some((error) => error.startsWith('index.html:')));
  assert.ok(result.errors.some((error) => error.startsWith('404.html:') && error.includes('missing.svg')));
});

test('reports malformed URLs and percent encoding', async (t) => {
  const directory = await fixture(t, {
    ...searchAssets,
    'index.html': '<a href="http://[invalid">Invalid URL</a><a href="guide/#%ZZ">Invalid encoding</a>',
    'guide/index.html': '',
  });
  const result = await validateSite(directory);
  assert.equal(result.errors.length, 2);
  assert.ok(result.errors.some((error) => error.includes('invalid URL "')));
  assert.ok(result.errors.some((error) => error.includes('invalid URL encoding')));
});
