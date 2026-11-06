// Журналы счетов, которых нет среди действующих услуг в карточках (закрытые услуги с остатком).
// Страница счёта открывается тем же обработчиком, что и в списке лицевых счетов. Только чтение.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { join } from 'node:path';
import { connect, openTab } from './cdp.mjs';

const root = process.argv[2];
const out = join(root, 'raw', 'cards', 'closed');
mkdirSync(out, { recursive: true, mode: 0o700 });
const inventory = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const known = new Set(readFileSync(join(root, 'normalized', 'abonents.jsonl'), 'utf8').trim().split('\n').flatMap((line) => JSON.parse(line).services.map((s) => s.account)));
const state = JSON.parse(readFileSync(join(root, 'raw', 'Reference_Account_AccountList.state.json'), 'utf8'));
const head = state.head.map((title) => title.replace(/[ ↑↓]+$/, ''));
const targets = readFileSync(join(root, 'raw', 'Reference_Account_AccountList.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line))
  .map((row) => ({ ...row, values: Object.fromEntries(head.map((title, index) => [title, row.cells[index]])) }))
  .filter((row) => row.values['Номер']?.startsWith('RUB.') && !known.has(row.values['Номер']) && !/^-?0([.,]0+)?$/.test((row.values['Баланс'] || '0').replace(/\s/g, '')));
console.log('счетов вне карточек с остатком', targets.length);

const list = await openTab(inventory.find((item) => item.path.includes('Reference/Account/AccountList')).url);
await new Promise((r) => setTimeout(r, 4000));
for (let i = 0; i < 20; i += 1) { if (await list.evaluate("document.readyState === 'complete' && typeof curEdit === 'function' && !!document.forms['XREF']")) break; await new Promise((r) => setTimeout(r, 1000)); }
const page = await connect();
const now = new Date();
const range = `fromyear=2000&frommonth=1&fromday=1&toyear=${now.getFullYear()}&tomonth=${now.getMonth() + 1}&today=${now.getDate()}`;
let saved = 0;
for (const row of targets) {
  const number = row.values['Номер'];
  const url = await list.evaluate(`(() => { const opened = []; const keep = window.open; window.open = (u) => { opened.push(u); return { focus() {}, document: {} }; }; try { curEdit(${JSON.stringify(row.data.id)}, ${JSON.stringify(row.data.name || '')}, ''); } catch (e) {} window.open = keep; return opened[0] ? new URL(opened[0], location.href).href : ''; })()`);
  if (!url) { console.log('нет ссылки на страницу счёта', JSON.stringify(Object.keys(row.data || {}))); continue; }
  const accountPage = JSON.parse(await page.evaluate(`(async () => { const r = await fetch(${JSON.stringify(url)}, { credentials: 'include' }); return JSON.stringify({ status: r.status, text: await r.text() }); })()`));
  const journal = (accountPage.text.match(/jsShowAccountJournal\('info','(journal\.csp\?[^']+)'/) || [])[1];
  const safe = number.replace(/[^A-Za-z0-9А-Яа-я.]/g, '_');
  writeFileSync(join(out, `${safe}_account.html.gz`), gzipSync(accountPage.text), { mode: 0o600 });
  if (!journal) { console.log('нет журнала на странице счёта'); continue; }
  const history = JSON.parse(await page.evaluate(`(async () => { const r = await fetch(${JSON.stringify(new URL(`${journal}&${range}`, 'https://gkh.nsk.ru/csp/gkhc2/').href)}, { credentials: 'include' }); return JSON.stringify({ status: r.status, text: await r.text() }); })()`));
  writeFileSync(join(out, `${safe}_journal.html.gz`), gzipSync(history.text), { mode: 0o600 });
  saved += 1;
  await new Promise((r) => setTimeout(r, 200));
}
console.log('журналов сохранено', saved);
await list.close();
page.close();
