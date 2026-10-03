import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ELEMENT_NODE, parse, walkSync } from 'ultrahtml';
import { site, base } from '../site.config.mjs';

function decodeEntities(value) {
  const named = { amp: '&', quot: '"', apos: "'", lt: '<', gt: '>' };
  return value.replace(/&(#x[\da-f]+|#\d+|amp|quot|apos|lt|gt);/gi, (_, entity) => {
    if (entity.startsWith('#')) {
      const codePoint = entity[1].toLowerCase() === 'x'
        ? Number.parseInt(entity.slice(2), 16)
        : Number.parseInt(entity.slice(1), 10);
      return String.fromCodePoint(codePoint);
    }
    return named[entity.toLowerCase()];
  });
}

async function listFiles(directory, prefix = '') {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relative = prefix + entry.name;
    if (entry.isDirectory()) {
      files.push(...await listFiles(path.join(directory, entry.name), relative + '/'));
    } else if (entry.isFile()) {
      files.push(relative);
    }
  }
  return files;
}

export async function validateSite(directory, { origin = site, basePath = base } = {}) {
  const files = new Set(await listFiles(directory));
  const documents = new Map();
  const references = [];
  const errors = [];
  const prefix = basePath.replace(/\/$/, '') + '/';

  for (const file of files) {
    if (file.endsWith('.html')) {
      const ids = new Set();
      const html = await readFile(path.join(directory, file), 'utf8');
      walkSync(parse(html), (node) => {
        if (node.type !== ELEMENT_NODE) return;
        const attributes = node.attributes;
        if (attributes.id) ids.add(decodeEntities(attributes.id));
        if (node.name === 'a' && attributes.name) ids.add(decodeEntities(attributes.name));
        // Starlight's /404/ canonical isn't the 404.html file that Pages serves.
        if (file === '404.html' && node.name === 'link' && attributes.rel === 'canonical') return;
        for (const attribute of ['href', 'src', 'poster']) {
          if (attributes[attribute]) {
            references.push({ file, value: decodeEntities(attributes[attribute]) });
          }
        }
        if (attributes.srcset && !attributes.srcset.startsWith('data:')) {
          for (const candidate of attributes.srcset.split(',')) {
            references.push({ file, value: decodeEntities(candidate.trim().split(/\s+/)[0]) });
          }
        }
      });
      documents.set(file, ids);
    } else if (file.endsWith('.css')) {
      const css = await readFile(path.join(directory, file), 'utf8');
      for (const match of css.matchAll(/url\(\s*(?:"([^"]*)"|'([^']*)'|([^)]*?))\s*\)/g)) {
        references.push({ file, value: match[1] ?? match[2] ?? match[3] });
      }
    }
  }

  if (documents.size === 0) errors.push('No generated HTML pages found.');
  for (const asset of ['pagefind/pagefind.js', 'pagefind/pagefind-entry.json']) {
    if (!files.has(asset)) errors.push(`Missing search asset: ${asset}`);
  }
  if (![...files].some((file) => file.startsWith('pagefind/') && file.endsWith('.pf_index'))) {
    errors.push('Missing Pagefind search index.');
  }

  for (const { file, value } of references) {
    const source = new URL(prefix + file, origin);
    let target;
    try {
      target = new URL(value, source);
    } catch {
      errors.push(`${file}: invalid URL "${value}"`);
      continue;
    }
    if (target.origin !== source.origin || !['http:', 'https:'].includes(target.protocol)) continue;
    if (!target.pathname.startsWith(prefix)) {
      errors.push(`${file}: URL is outside the Pages base path: "${value}"`);
      continue;
    }

    let relative;
    let fragment;
    try {
      relative = decodeURIComponent(target.pathname.slice(prefix.length));
      fragment = decodeURIComponent(target.hash.slice(1)).split(':~:text=')[0];
    } catch {
      errors.push(`${file}: invalid URL encoding "${value}"`);
      continue;
    }
    if (relative.endsWith('/') || relative === '') relative += 'index.html';
    if (!files.has(relative) && files.has(relative + '/index.html')) relative += '/index.html';
    if (!files.has(relative)) {
      errors.push(`${file}: missing local target "${value}"`);
    } else if (fragment && documents.has(relative) && !documents.get(relative).has(fragment)) {
      errors.push(`${file}: missing anchor "${value}"`);
    }
  }

  return { htmlCount: documents.size, referenceCount: references.length, errors };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = await validateSite(fileURLToPath(new URL('../dist/', import.meta.url)));
  if (result.errors.length) {
    console.error(result.errors.join('\n'));
    process.exitCode = 1;
  } else {
    console.log(`Validated ${result.htmlCount} pages, ${result.referenceCount} references, and the search index.`);
  }
}
