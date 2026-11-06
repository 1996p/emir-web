// История льготников: сервер отдаёт не больше 100 строк на любой запрос. Фильтры накладываются по очереди
// (год, месяц, тип, населённый пункт, улица), пока кусок не станет меньше лимита. Только чтение.
import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { openTab } from './cdp.mjs';

const root = process.argv[2];
const CAP = 100;
const items = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const item = items.find((entry) => entry.name.startsWith('История льготников'));
const dataFile = join(root, 'raw', 'benefit-history.jsonl');
const stateFile = join(root, 'raw', 'benefit-history.state.json');
const state = existsSync(stateFile) ? JSON.parse(readFileSync(stateFile, 'utf8')) : { done: [], rows: 0, capped: [] };
const done = new Set(state.done);
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let tab;
const open = async () => {
  tab = await openTab(item.url);
  for (let i = 0; i < 25; i += 1) { await wait(1500); try { if (await tab.evaluate("!!(window._zenIndex||[]).filter(Boolean).find(c=>c._type==='tablePane')")) return; } catch {} }
  throw new Error('нет таблицы');
};
await open();
// Значения фильтров берутся из самой страницы: выпадающие списки над таблицей.
const options = JSON.parse(await tab.evaluate("JSON.stringify(Object.fromEntries([...document.querySelectorAll('select[id^=filter1_]')].map(s=>[s.id.replace(/^filter1_/,'').replace(/_\\d+$/,''), [...s.options].map(o=>o.value).filter(Boolean)])))"));
const levels = ['YEARS', 'MONTHS', 'Request_Type', 'CITY', 'STREET'].filter((name) => options[name]?.length);
const columns = JSON.parse(await tab.evaluate("JSON.stringify(_zenIndex.filter(Boolean).find(c=>c._type==='tablePane').columns.filter(c=>!c.hidden).map(c=>c.colName))"));
const queue = [{}];
let queries = 0;
while (queue.length) {
  const filter = queue.shift();
  const key = JSON.stringify(filter);
  if (done.has(key)) continue;
  await tab.evaluate(`(()=>{const t=_zenIndex.filter(Boolean).find(c=>c._type==='tablePane'); const f=${key}; for (const c of t.columns) c.filterValue = f[c.colName] ?? ''; t.rowCount='__p'; t.executeQuery(false); return 1;})()`);
  let count = '__p';
  for (let i = 0; i < 80 && count === '__p'; i += 1) { await wait(1200); count = await tab.evaluate("String(_zenIndex.filter(Boolean).find(c=>c._type==='tablePane').rowCount)"); }
  queries += 1;
  if (count === '__p') { await tab.close(); await open(); queue.unshift(filter); continue; }
  const depth = Object.keys(filter).length;
  if (Number(count) >= CAP && depth < levels.length) {
    const level = levels[depth];
    for (const value of [...options[level]].reverse()) queue.unshift({ ...filter, [level]: value });
  } else {
    if (Number(count) >= CAP) state.capped.push(filter);
    if (Number(count) > 0) {
      await wait(500);
      const rows = JSON.parse(await tab.evaluate(`JSON.stringify([..._zenIndex.filter(Boolean).find(c=>c._type==='tablePane').getEnclosingDiv().querySelectorAll('tr[zenValue]')].map(r=>({ value: r.getAttribute('zenValue'), cells: [...r.cells].slice(2).map(c=>c.textContent.replace(/\\s+/g,' ').trim()) })))`));
      if (rows.length) appendFileSync(dataFile, rows.map((row) => JSON.stringify({ filter, ...row })).join('\n') + '\n', { mode: 0o600 });
      state.rows += rows.length;
    }
  }
  done.add(key); state.done = [...done]; state.columns = columns;
  writeFileSync(stateFile, JSON.stringify(state), { mode: 0o600 });
  if (queries % 50 === 0) console.log(`запросов ${queries}, строк ${state.rows}, упёрлось в лимит ${state.capped.length}, в очереди ${queue.length}`);
}
console.log(`готово: строк ${state.rows}, запросов ${queries}, упёрлось в лимит ${state.capped.length}`);
await tab.close();
