// Копирует постраничные списки старого ЕМИР в локальные файлы. Только чтение: открываются лишь списки из описи.
// В консоль выводятся только счётчики, строки с данными остаются в файлах.
// Запуск: node collect-lists.mjs <папка> [часть пути списка ...]
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { connect } from './cdp.mjs';

const root = process.argv[2];
const only = process.argv.slice(3);
const raw = join(root, 'raw');
mkdirSync(raw, { recursive: true, mode: 0o700 });
const PAUSE_MS = 250;
const PER_PAGE = 300;
// Для списков с выбором дат: EMIR_LIST_PARAMS="fromyear=2000&frommonth=1&fromday=1".
const EXTRA = process.env.EMIR_LIST_PARAMS ? `&${process.env.EMIR_LIST_PARAMS}` : '';

const items = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line))
  .filter((item) => item.kind === 'query' && (only.length === 0 || only.some((part) => item.path.includes(part))));
const page = await connect();

/** Одна страница списка: заголовки, ячейки и ссылки каждой строки. */
const readPage = (url) => page.evaluate(`(async () => {
  const reply = await __get(${JSON.stringify(url)});
  const doc = __parse(reply.text);
  if (doc.querySelector('input[type=password]')) return JSON.stringify({ loggedOut: true });
  // Заголовок бывает и в th, и в обычных ячейках со ссылками сортировки.
  const isHead = (row) => row.querySelector('th') || row.querySelector('a[href*=_ORDER]');
  const tables = [...doc.querySelectorAll('table')];
  // Таблица с выбором дат тоже бывает с заголовком: таблицы с полями формы не берутся.
  const plain = tables.filter((t) => !t.querySelector('select, input:not([type=hidden])') && !t.querySelector('table'));
  const marked = plain.filter((t) => [...t.rows].some(isHead)).sort((a, b) => b.rows.length - a.rows.length)[0];
  const largest = plain.sort((a, b) => b.rows.length - a.rows.length)[0];
  // Без явной шапки: строка заголовка это первая строка с полным числом ячеек, всё выше неё название таблицы.
  const table = marked && (!largest || marked.rows.length * 2 > largest.rows.length) ? marked : largest;
  const width = table ? Math.max(...[...table.rows].map((r) => r.cells.length)) : 0;
  // Шапка такой таблицы узнаётся по стрелке сортировки, данные это строки с полным числом ячеек ниже неё.
  const arrowRow = table && table !== marked ? [...table.rows].findIndex((r) => /[↑↓]/.test(r.textContent)) : -1;
  const rows = [];
  let head = [];
  if (table) for (const [index, row] of [...table.rows].entries()) {
    if (arrowRow >= 0 && index < arrowRow) continue;
    if (isHead(row) || index === arrowRow) { if (!head.length) head = [...row.cells].map((c) => __clean(c.textContent)); continue; }
    if (table !== marked && row.cells.length !== width) continue;
    rows.push({ data: { ...row.dataset }, cells: [...row.cells].map((c) => __clean(c.textContent)), links: [...row.querySelectorAll('a[href]')].map((a) => [__clean(a.textContent), a.getAttribute('href')]) });
  }
  return JSON.stringify({ status: reply.status, pages: Number((reply.text.match(/Выберите страницу \\(1 - (\\d+)\\)/) || [])[1] || 1), head, rows });
})()`);

const slugs = new Set();
const summary = [];
for (const item of items) {
  const slug = item.path.replace(/\.csp$/, '').replace(/[^A-Za-z0-9]+/g, '_');
  if (slugs.has(slug)) continue;
  slugs.add(slug);
  const dataFile = join(raw, `${slug}.jsonl`);
  const stateFile = join(raw, `${slug}.state.json`);
  const state = existsSync(stateFile) ? JSON.parse(readFileSync(stateFile, 'utf8')) : { name: item.name, section: item.section, done: 0, pages: null, rows: 0, head: [] };
  if (state.pages !== null && state.done >= state.pages) { summary.push([item.name, state.rows, 'уже есть']); continue; }
  const separator = item.url.includes('?') ? '&' : '?';
  let pageNumber = state.done + 1;
  let pages = state.pages ?? 1;
  let errors = 0;
  while (pageNumber <= pages) {
    const url = item.query
      ? `${item.url}${separator}${item.query}_ONPAGE=${PER_PAGE}&${item.query}_PAGE=${pageNumber}${EXTRA}`
      : item.url;
    let reply;
    try {
      reply = JSON.parse(await readPage(url));
    } catch {
      if (++errors > 3) break;
      await new Promise((resolve) => setTimeout(resolve, 5000 * errors));
      continue;
    }
    if (reply.loggedOut) { console.log('Сессия старого ЕМИР закончилась, нужен повторный вход. Готовое сохранено.'); page.close(); process.exit(2); }
    if (reply.status !== 200) { if (++errors > 3) break; await new Promise((resolve) => setTimeout(resolve, 5000 * errors)); continue; }
    errors = 0;
    pages = reply.pages;
    if (reply.head.length) state.head = reply.head;
    if (reply.rows.length) appendFileSync(dataFile, reply.rows.map((row) => JSON.stringify({ page: pageNumber, ...row })).join('\n') + '\n', { mode: 0o600 });
    state.rows += reply.rows.length;
    state.done = pageNumber;
    state.pages = pages;
    writeFileSync(stateFile, JSON.stringify(state), { mode: 0o600 });
    pageNumber += 1;
    await new Promise((resolve) => setTimeout(resolve, PAUSE_MS));
  }
  const complete = state.pages !== null && state.done >= state.pages;
  summary.push([item.name, state.rows, complete ? `страниц ${state.pages}` : `НЕ ДОКАЧАНО ${state.done}/${state.pages}`]);
  console.log(summary.at(-1).join(' | '));
}
writeFileSync(join(root, 'lists-summary.json'), JSON.stringify(summary), { mode: 0o600 });
console.log('готово', summary.length);
page.close();
