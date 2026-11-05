// Открывает отчёты с формой GET за заданный период и сохраняет страницу как есть. Только чтение, отложенные отчёты не заказываются.
// В консоль выводятся заголовки таблиц и счётчики строк.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { join } from 'node:path';
import { connect } from './cdp.mjs';

const root = process.argv[2];
const out = join(root, 'raw', 'reports');
mkdirSync(out, { recursive: true, mode: 0o700 });
const inventory = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const page = await connect();

const run = async (name, path, values) => {
  const item = inventory.find((entry) => entry.path.includes(path));
  const result = JSON.parse(await page.evaluate(`(async () => {
    const start = __parse((await __get(${JSON.stringify(item.url)})).text);
    const form = start.forms[0];
    if (!form || ${JSON.stringify(!!values._plain)}) { const whole = await __get(${JSON.stringify(item.url)}); return JSON.stringify({ status: whole.status, text: whole.text, tables: [], fields: [] }); }
    if (form.method.toLowerCase() !== 'get') return JSON.stringify({ refused: form.method });
    const params = new URLSearchParams();
    for (const el of form.elements) {
      if (!el.name || el.type === 'submit' || el.type === 'button') continue;
      if (el.type === 'radio' || el.type === 'checkbox') { if (el.checked) params.append(el.name, el.value); continue; }
      if (el.type === 'select-multiple') { for (const o of el.options) params.append(el.name, o.value); continue; }
      params.append(el.name, el.value);
    }
    const overrides = ${JSON.stringify(values)};
    for (const [k, v] of Object.entries(overrides)) params.set(k, v);
    const action = new URL(form.getAttribute('action') || location.href, ${JSON.stringify(item.url)});
    for (const [k, v] of params) action.searchParams.set(k, v);
    const multi = [...form.elements].filter((el) => el.type === 'select-multiple').map((el) => el.name);
    for (const m of multi) { action.searchParams.delete(m); for (const v of params.getAll(m)) action.searchParams.append(m, v); }
    const reply = await __get(action.href);
    const doc = __parse(reply.text);
    const tables = [...doc.querySelectorAll('table')].map((t) => ({ rows: t.rows.length, head: [...t.rows].slice(0, 3).map((r) => [...r.cells].slice(0, 16).map((c) => { const s = __clean(c.textContent); return s.length > 28 || /[А-ЯЁ][а-яё]+\\s+[А-ЯЁ][а-яё]+/.test(s) ? '…' : s.replace(/\\d/g, '9'); })) })).sort((a, b) => b.rows - a.rows).slice(0, 2);
    return JSON.stringify({ status: reply.status, text: reply.text, tables, fields: [...params.keys()].filter((k, i, a) => a.indexOf(k) === i) });
  })()`));
  if (result.refused) { console.log(name, 'форма не GET, пропущено'); return; }
  writeFileSync(join(out, `${name}.html.gz`), gzipSync(result.text), { mode: 0o600 });
  console.log(name, result.status, result.text.length, JSON.stringify(result.fields), JSON.stringify(result.tables));
};

const now = new Date();
const to = { toyear: String(now.getFullYear()), tomonth: String(now.getMonth() + 1) };
for (const [name, path, values] of JSON.parse(process.argv[3])) await run(name, path, { ...to, ...values });
console.log('готово');
page.close();
