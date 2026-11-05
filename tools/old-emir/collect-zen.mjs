// Копирует списки Zen (таблицы, которые живут в браузере) в локальные файлы. Только чтение: таблице задаётся
// большой лимит и выполняется её собственный запрос. В консоль выводятся только счётчики.
// Запуск: node collect-zen.mjs <папка> [название списка ...]
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { openTab } from './cdp.mjs';

const root = process.argv[2];
const only = process.argv.slice(3);
const raw = join(root, 'raw');
mkdirSync(raw, { recursive: true, mode: 0o700 });
const LIMIT = 500000;
const WAIT_MS = 280000;
// Поисковые формы и приём оплаты не списки; КЛАДР общедоступен и огромен; журнал ошибок для учёта не нужен.
const skip = /^Поиск$|^Прием оплаты|Кладр|^Ошибки$/;

const seen = new Set();
const items = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line))
  .filter((item) => item.kind === 'zen' && !skip.test(item.name) && (only.length === 0 || only.includes(item.name)));

for (const item of items) {
  const slug = 'zen_' + (item.section + '_' + item.name).replace(/[^A-Za-zА-Яа-яЁё0-9]+/g, '_');
  if (seen.has(slug)) continue;
  seen.add(slug);
  const dataFile = join(raw, `${slug}.jsonl`);
  const stateFile = join(raw, `${slug}.state.json`);
  if (existsSync(stateFile) && JSON.parse(readFileSync(stateFile, 'utf8')).complete) { console.log(`${item.name} | уже есть`); continue; }
  const tab = await openTab(item.url);
  try {
    for (let attempt = 0; attempt < 15; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 1500));
      if (await tab.evaluate(`!!(window._zenIndex || []).filter(Boolean).find((c) => c._type === 'tablePane') || !!document.querySelector('input[type=password]')`)) break;
    }
    const start = await tab.evaluate(`(() => {
      if (document.querySelector('input[type=password]')) return 'loggedOut';
      const t = (window._zenIndex || []).filter(Boolean).find((c) => c._type === 'tablePane');
      if (!t) return 'noTable';
      window.__t = t;
      window.__rows = () => { const box = t.getEnclosingDiv(); const table = [...box.querySelectorAll('table')].sort((a, b) => b.rows.length - a.rows.length)[0]; return table ? [...table.rows].filter((r) => r.closest('tbody') && !r.querySelector('th')) : []; };
      t.rowCount = '__pending'; t.maxRows = ${LIMIT}; t.pageSize = ${LIMIT}; t.executeQuery(false);
      return 'started';
    })()`);
    if (start === 'loggedOut') { console.log('Сессия старого ЕМИР закончилась, нужен повторный вход.'); await tab.close(); process.exit(2); }
    if (start === 'noTable') { console.log(`${item.name} | нет таблицы`); continue; }
    const deadline = Date.now() + WAIT_MS;
    let progress = { rowCount: '__pending', rows: 0 };
    while (Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 2000));
      progress = JSON.parse(await tab.evaluate(`JSON.stringify({ rowCount: String(__t.rowCount), rows: __rows().length })`));
      if (progress.rowCount !== '__pending') break;
    }
    if (progress.rowCount === '__pending') { console.log(`${item.name} | НЕ ДОЖДАЛСЯ`); writeFileSync(stateFile, JSON.stringify({ name: item.name, complete: false }), { mode: 0o600 }); continue; }
    await new Promise((resolve) => setTimeout(resolve, 1500));
    const data = JSON.parse(await tab.evaluate(`JSON.stringify((() => {
      const clean = (v) => (v || '').replace(/\\s+/g, ' ').trim();
      const columns = (__t.columns || []).map((c) => ({ name: c.colName, header: c.header, hidden: c.hidden }));
      const box = __t.getEnclosingDiv();
      const head = [...box.querySelectorAll('th')].map((c) => clean(c.textContent));
      const rows = __rows().filter((r) => r.getAttribute('zenValue') !== null || r.id || r.cells.length > 1).map((r) => ({ value: r.getAttribute('zenValue'), cells: [...r.cells].map((c) => clean(c.textContent)), links: [...r.querySelectorAll('a[href]')].map((a) => [clean(a.textContent), a.getAttribute('href')]) }));
      return { rowCount: String(__t.rowCount), columns, head, rows };
    })())`));
    writeFileSync(dataFile, data.rows.map((row) => JSON.stringify(row)).join('\n') + (data.rows.length ? '\n' : ''), { mode: 0o600 });
    const declared = Number(data.rowCount);
    const complete = !data.rowCount.includes('+') && (Number.isNaN(declared) || declared === data.rows.length);
    writeFileSync(stateFile, JSON.stringify({ name: item.name, section: item.section, rowCount: data.rowCount, rows: data.rows.length, complete, columns: data.columns, head: data.head }), { mode: 0o600 });
    console.log(`${item.name} | заявлено ${data.rowCount} | получено ${data.rows.length}${complete ? '' : ' | НЕПОЛНО'}`);
  } catch (error) {
    console.log(`${item.name} | ОШИБКА ${String(error.message).slice(0, 100)}`);
  } finally {
    await tab.close();
  }
  await new Promise((resolve) => setTimeout(resolve, 500));
}
console.log('готово');
