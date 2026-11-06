// Zen-список, который не отдаётся целиком: фильтр колонки по началу значения (например, дате),
// куски дробятся дальше, пока каждый не станет меньше лимита. Только чтение, в консоль идут счётчики.
// Запуск: node collect-zen-split.mjs <папка> "<название списка>" <колонка> <начальные значения через запятую>
import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { openTab } from './cdp.mjs';

const [root, name, column, seeds] = process.argv.slice(2);
const LIMIT = 5000;
// Некоторые списки сервер обрезает сам (например, на 1000 строк): такой кусок тоже дробится.
const CAP = Number(process.env.EMIR_SPLIT_CAP ?? LIMIT);
const ALPHABET = process.env.EMIR_SPLIT_ALPHABET ?? '0123456789-';
const MAX_DEPTH = Number(process.env.EMIR_SPLIT_DEPTH ?? 10);
const items = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const slug = 'zensplit_' + name.replace(/[^A-Za-zА-Яа-яЁё0-9]+/g, '_');
const dataFile = join(root, 'raw', `${slug}.jsonl`);
const stateFile = join(root, 'raw', `${slug}.state.json`);
const state = existsSync(stateFile) ? JSON.parse(readFileSync(stateFile, 'utf8')) : { done: [], rows: 0, capped: [] };
const done = new Set(state.done);
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let tab = null;
const open = async () => {
  tab = await openTab(items.find((item) => item.name === name).url);
  for (let i = 0; i < 25; i += 1) { await wait(1500); try { if (await tab.evaluate("!!(window._zenIndex||[]).filter(Boolean).find(c=>c._type==='tablePane')")) return; } catch {} }
  throw new Error('нет таблицы');
};
await open();
const columns = JSON.parse(await tab.evaluate("JSON.stringify(_zenIndex.filter(Boolean).find(c=>c._type==='tablePane').columns.filter(c=>!c.hidden).map(c=>c.colName))"));
const queue = seeds.split(',');
while (queue.length) {
  const prefix = queue.shift();
  if (done.has(prefix)) continue;
  await tab.evaluate(`(()=>{const t=_zenIndex.filter(Boolean).find(c=>c._type==='tablePane'); for (const c of t.columns) c.filterValue=''; t.columns.find(x=>x.colName===${JSON.stringify(column)}).filterValue=${JSON.stringify(prefix)}; t.maxRows=${LIMIT}; t.pageSize=${LIMIT}; t.rowCount='__p'; t.executeQuery(false); return 1;})()`);
  let count = '__p';
  for (let i = 0; i < 120 && count === '__p'; i += 1) { await wait(1500); count = await tab.evaluate("String(_zenIndex.filter(Boolean).find(c=>c._type==='tablePane').rowCount)"); }
  if (count === '__p') { console.log(prefix, 'не дождался, переоткрываю'); await tab.close(); await open(); queue.unshift(prefix); continue; }
  if (count.includes('+') || Number(count) >= Math.min(LIMIT, CAP)) {
    if (prefix.length >= MAX_DEPTH) { state.capped.push(prefix); console.log(prefix, 'всё ещё больше лимита'); }
    else for (const next of ALPHABET) queue.unshift(prefix + next);
    done.add(prefix); state.done = [...done]; writeFileSync(stateFile, JSON.stringify(state), { mode: 0o600 });
    continue;
  }
  await wait(800);
  const rows = JSON.parse(await tab.evaluate(`JSON.stringify([..._zenIndex.filter(Boolean).find(c=>c._type==='tablePane').getEnclosingDiv().querySelectorAll('tr[zenValue]')].map(r=>({ value: r.getAttribute('zenValue'), cells: [...r.cells].slice(2).map(c=>c.textContent.replace(/\\s+/g,' ').trim()) })))`));
  if (rows.length) appendFileSync(dataFile, rows.map((row) => JSON.stringify({ prefix, ...row })).join('\n') + '\n', { mode: 0o600 });
  state.rows += rows.length; state.columns = columns;
  done.add(prefix); state.done = [...done]; writeFileSync(stateFile, JSON.stringify(state), { mode: 0o600 });
  if (rows.length !== Number(count || 0)) console.log(prefix, 'заявлено', count, 'получено', rows.length);
}
console.log(`готово: строк ${state.rows}, кусков ${done.size}, больше лимита ${state.capped.length}`);
await tab.close();
