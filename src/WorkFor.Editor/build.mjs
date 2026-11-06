import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const root = path.dirname(fileURLToPath(import.meta.url));
const output = path.resolve(root, '../../out/editor');
await fs.mkdir(output, { recursive: true });
await build({
  absWorkingDir: root, entryPoints: { main: 'main.mjs', 'editor.worker': 'monaco-editor/editor/editor.worker.js' },
  bundle: true, outdir: output, format: 'iife', target: 'es2022', minify: true, legalComments: 'external',
  loader: { '.ttf': 'file' }, assetNames: 'assets/[name]-[hash]', logLevel: 'info',
});
await fs.copyFile(path.join(root, 'index.html'), path.join(output, 'index.html'));
await fs.copyFile(path.resolve(root, '../../sdk/workfor-api.d.ts'), path.join(output, 'workfor-api.d.ts'));
await fs.mkdir(path.join(output, 'lsp'), { recursive: true });
await fs.cp(path.resolve(root, '../WorkFor.LanguageServer/node_modules'), path.join(output, 'lsp/node_modules'), { recursive: true });
const runtime = path.resolve(root, '../../.tools/node/node-v26.13.0-win-x64');
await fs.copyFile(path.join(runtime, 'node.exe'), path.join(output, 'lsp/node.exe'));
await fs.copyFile(path.join(runtime, 'LICENSE'), path.join(output, 'lsp/Node-LICENSE.txt'));
await fs.copyFile(path.resolve(root, '../WorkFor.LanguageServer/package-lock.json'), path.join(output, 'lsp/package-lock.json'));
for (const name of ['monaco-editor', 'marked', 'dompurify', 'acorn', 'acorn-loose']) {
  const folder = path.join(root, 'node_modules', name);
  const metadata = JSON.parse(await fs.readFile(path.join(folder, 'package.json'), 'utf8'));
  const license = (await fs.readdir(folder)).find(file => /^licen[sc]e(?:\.|$)/i.test(file));
  if (!license) throw new Error(`Missing distribution license for ${name}`);
  await fs.copyFile(path.join(folder, license), path.join(output, `${name}-LICENSE.txt`));
  await fs.writeFile(path.join(output, `${name}-package.json`), JSON.stringify({ name, version: metadata.version, license: metadata.license }, null, 2));
}
