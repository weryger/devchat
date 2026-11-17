import { build } from 'esbuild';
import { cp, mkdir, rm } from 'node:fs/promises';

const watch = process.argv.includes('--watch');

if (!watch) await rm('dist', { recursive: true, force: true });
await mkdir('dist', { recursive: true });
await cp('public', 'dist', { recursive: true });

await build({
  entryPoints: { background: 'src/background.ts' },
  bundle: true,
  format: 'esm',
  outfile: 'dist/background.js',
  platform: 'browser',
  target: 'chrome120',
  // мёртвые ветки в lib playwright-crx (dev-workspace модули, в npm-пакете их нет);
  // в ESM-выводе превращаются в throw-при-вызове, до которого мы не доходим
  external: ['../playwright', './bidiOverCdp'],
  sourcemap: false,
  minify: false,
  logLevel: 'info',
  ...(watch ? { watch: {} } : {}),
});

console.log(watch ? '→ watch-режим: dist/ обновляется, перезагрузи расширение в chrome://extensions' : '→ готово: загрузи dist/ в chrome://extensions (Распакованное расширение)');
