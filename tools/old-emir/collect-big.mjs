// Докачивает журналы, не уместившиеся в лимит за год: тот же журнал помесячно. Только чтение.
// Запуск: node collect-big.mjs <папка>. Вход: raw/cards.big.log, строки «абонент индекс год».
import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { gunzipSync, gzipSync } from 'node:zlib';
import { join } from 'node:path';
import { connect } from './cdp.mjs';

const root = process.argv[2];
const cards = join(root, 'raw', 'cards');
const lines = readFileSync(join(root, 'raw', 'cards.big.log'), 'utf8').trim().split('\n').filter(Boolean).map((line) => line.split(' '));
const base = 'https://gkh.nsk.ru/csp/gkhc2/';
const WAIT_MS = 170000;
let page = await connect();
const fetchPage = async (url) => {
  const key = `b${Date.now()}`;
  const canceller = setTimeout(() => { page.evaluate(`window.__stops && window.__stops[${JSON.stringify(key)}] && window.__stops[${JSON.stringify(key)}].abort(); 1`).catch(() => {}); }, WAIT_MS);
  try {
    return JSON.parse(await page.evaluate(`(async () => {
      const stop = new AbortController();
      (window.__stops ??= {})[${JSON.stringify(key)}] = stop;
      try {
        const response = await fetch(${JSON.stringify(url)}, { credentials: 'include', signal: stop.signal });
        const text = await response.text();
        return JSON.stringify({ status: response.status, loggedOut: /<input[^>]+type=.?password/i.test(text), text });
      } catch (error) {
        return JSON.stringify({ status: error.name === 'AbortError' ? 'timeout' : 'failed' });
      } finally { delete window.__stops[${JSON.stringify(key)}]; }
    })()`));
  } catch {
    page = await connect();
    return { status: 'failed' };
  } finally { clearTimeout(canceller); }
};
const seen = new Set();
let saved = 0;
let failed = 0;
for (const [id, index, year] of lines) {
  const mark = `${id}_${index}_${year}`;
  if (seen.has(mark)) continue;
  seen.add(mark);
  const account = gunzipSync(readFileSync(join(cards, `${id}_account_${index}.html.gz`))).toString('utf8');
  const journal = (account.match(/jsShowAccountJournal\('info','(journal\.csp\?[^']+)'/) || [])[1];
  if (!journal) { console.log('нет ссылки на журнал', id, index); failed += 1; continue; }
  for (let month = 1; month <= 12; month += 1) {
    const file = join(cards, `${id}_journal_${index}_${year}${String(month).padStart(2, '0')}.html.gz`);
    if (existsSync(file)) continue;
    const last = new Date(Number(year), month, 0).getDate();
    const reply = await fetchPage(new URL(`${journal}&fromyear=${year}&frommonth=${month}&fromday=1&toyear=${year}&tomonth=${month}&today=${last}`, base).href);
    if (reply.loggedOut) { console.log('Сессия закончилась'); process.exit(2); }
    if (reply.status !== 200) { failed += 1; appendFileSync(join(root, 'raw', 'cards.big2.log'), `${id} ${index} ${year} ${month} ${reply.status}\n`, { mode: 0o600 }); continue; }
    writeFileSync(file, gzipSync(reply.text), { mode: 0o600 });
    saved += 1;
  }
  console.log(`журнал ${seen.size}: месяцев сохранено ${saved}, ошибок ${failed}`);
}
console.log(`готово: месяцев ${saved}, ошибок ${failed}`);
page.close();
