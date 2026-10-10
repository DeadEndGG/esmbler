// Fails when a Markdown file links to a relative path that does not exist. External links are not fetched.
import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';

const root = resolve(dirname(new URL(import.meta.url).pathname), '..');
const skip = new Set(['node_modules', '.git', 'bin', 'obj', 'target', 'dist', '.angular']);
const files = [];
(function walk(dir) {
  for (const name of readdirSync(dir)) {
    if (skip.has(name)) continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p);
    else if (name.endsWith('.md')) files.push(p);
  }
})(root);

let broken = 0;
// Every doc under docs/ (except requirement sources and generated files) needs front matter (execution brief section 7).
const required = ['title', 'status', 'owner', 'last_reviewed', 'requirements'];
const exempt = new Set(['docs/spec/master-spec.md', 'docs/spec/execution-brief.md']);
for (const file of files) {
  const rel = file.slice(root.length + 1);
  if (!rel.startsWith('docs/') || exempt.has(rel)) continue;
  const head = readFileSync(file, 'utf8').match(/^---\n([\s\S]*?)\n---/);
  const missing = head ? required.filter((k) => !new RegExp(`^${k}:`, 'm').test(head[1])) : required;
  if (missing.length) {
    console.error(`${rel}: front matter missing ${missing.join(', ')}`);
    broken++;
  }
}
const link = /\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
for (const file of files) {
  const text = readFileSync(file, 'utf8').replace(/```[\s\S]*?```/g, '');
  for (const m of text.matchAll(link)) {
    const target = m[1];
    if (/^(https?:|mailto:|#)/.test(target)) continue;
    const path = decodeURIComponent(target.split('#')[0]);
    if (!path) continue;
    if (!existsSync(resolve(dirname(file), path))) {
      console.error(`${file.slice(root.length + 1)}: broken link -> ${target}`);
      broken++;
    }
  }
}
console.log(`${files.length} Markdown files checked, ${broken} problems (broken links or missing front matter).`);
process.exit(broken ? 1 : 0);
