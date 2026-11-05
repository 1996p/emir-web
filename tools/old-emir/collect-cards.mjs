// Обходит абонентов: карточка → страницы счетов → журнал счёта за всё время.
// Страницы сохраняются как есть (gzip), разбор делается отдельно и может повторяться без старой системы.
// Открываются только чтением три вида страниц; кнопки не нажимаются, другие ссылки не открываются.
// Журналы пени огромные (операция на каждый день долга) и считаются минутами, поэтому идут вторым проходом.
// Запуск: node collect-cards.mjs <папка> [сколько абонентов]        первый проход: всё, кроме журналов пени
//         EMIR_PASS=penalty node collect-cards.mjs <папка>          второй проход: только журналы пени
import { existsSync, mkdirSync, readFileSync, writeFileSync, appendFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { join } from 'node:path';
import { connect, openTab } from './cdp.mjs';

const root = process.argv[2];
const limit = Number(process.argv[3] ?? Infinity);
const cards = join(root, 'raw', 'cards');
mkdirSync(cards, { recursive: true, mode: 0o700 });
const penaltyPass = process.env.EMIR_PASS === 'penalty';
const doneFile = join(root, 'raw', penaltyPass ? 'cards.penalty.done.jsonl' : 'cards.done.jsonl');
const PAUSE_MS = 80;
const now = new Date();
const range = `fromyear=2000&frommonth=1&fromday=1&toyear=${now.getFullYear()}&tomonth=${now.getMonth() + 1}&today=${now.getDate()}`;

const inventory = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const rawList = (name) => readFileSync(join(root, 'raw', `${name}.jsonl`), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
// Номера абонентов берутся из результатов поиска: номер в справочнике абонентов это другая нумерация.
const abonentIds = JSON.parse(readFileSync(join(root, 'raw', 'abonent-ids-search.json'), 'utf8')).filter((id) => /^\d+$/.test(id));
const done = new Set(existsSync(doneFile) ? readFileSync(doneFile, 'utf8').trim().split('\n').filter(Boolean).map((line) => JSON.parse(line).id) : []);

// Второй проход только по абонентам со счетами. EMIR_SHARD=i/n делит работу между параллельными запусками.
const firstPass = new Map(existsSync(join(root, 'raw', 'cards.done.jsonl')) ? readFileSync(join(root, 'raw', 'cards.done.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line)).map((row) => [row.id, row.pages]) : []);
const [shard, shards] = (process.env.EMIR_SHARD ?? '0/1').split('/').map(Number);
const queue = abonentIds.filter((id) => !done.has(id) && (!penaltyPass || firstPass.get(id) > 0)).filter((_, index) => index % shards === shard);

// Адрес карточки выдаёт сама система на странице результатов поиска.
const head = JSON.parse(readFileSync(join(root, 'raw', 'abonentlist.state.json'), 'utf8')).head;
const accountColumn = head.indexOf('ЛС');
const sample = rawList('abonentlist').find((row) => /\d/.test(row.cells[accountColumn] ?? '')).cells[accountColumn].split(/[ ,]/)[0];
const search = await openTab(inventory.find((item) => item.name === 'Поиск' && item.kind === 'zen').url);
await new Promise((resolve) => setTimeout(resolve, 4000));
await search.evaluate(`(() => { zenPage.Search('AccountNumber','Address','','',${JSON.stringify(sample)},'',''); return 1; })()`);
await new Promise((resolve) => setTimeout(resolve, 4000));
const results = await openTab(await search.evaluate(`document.querySelector('iframe').src`));
await new Promise((resolve) => setTimeout(resolve, 5000));
const cardBase = await results.evaluate(`new URL(zenPage.AbonentCardURL, location.href).href`);
await results.close();
await search.close();

let page = await connect();
// Журнал счёта кассира или организации может считаться минутами: такой запрос прерывается и дробится по годам.
const LONG_MS = 170000;
class TooLong extends Error {}
const fetchPage = async (url, wait = 60000, attempts = 4) => {
  for (let attempt = 1; ; attempt += 1) {
    try {
      // Таймеры скрытой вкладки Chrome притормаживает, поэтому отмену запроса отсчитывает сам скрипт.
      const key = `r${Date.now()}_${Math.random().toString(36).slice(2)}`;
      const canceller = setTimeout(() => { page.evaluate(`window.__stops && window.__stops[${JSON.stringify(key)}] && window.__stops[${JSON.stringify(key)}].abort(); 1`).catch(() => {}); }, wait);
      const reply = JSON.parse(await page.evaluate(`(async () => {
        const stop = new AbortController();
        (window.__stops ??= {})[${JSON.stringify(key)}] = stop;
        try {
          const response = await fetch(${JSON.stringify(url)}, { credentials: 'include', signal: stop.signal });
          const bytes = await response.arrayBuffer();
          const charset = (response.headers.get('content-type') || '').match(/charset=([^;]+)/i);
          const text = new TextDecoder(charset ? charset[1].trim() : 'utf-8').decode(bytes);
          return JSON.stringify({ status: response.status, loggedOut: /<input[^>]+type=.?password/i.test(text), text });
        } catch (error) {
          return JSON.stringify({ status: error.name === 'AbortError' ? 'timeout' : 'failed' });
        } finally { delete window.__stops[${JSON.stringify(key)}]; }
      })()`).finally(() => clearTimeout(canceller)));
      if (reply.status === 'timeout' && attempts === 1) throw new TooLong();
      if (reply.loggedOut) { console.log('Сессия старого ЕМИР закончилась, нужен повторный вход. Готовое сохранено.'); process.exit(2); }
      if (reply.status === 200) return reply.text;
      if (reply.status === 404) return null;
      throw new Error(`статус ${reply.status}`);
    } catch (error) {
      // Chrome рвёт соединение на слишком большом ответе: подключаемся заново, а журнал уйдёт в разбивку по годам.
      if (String(error.message).includes('Вкладка закрыта')) { page = await connect(); if (attempts === 1) throw new TooLong(); }
      if (error instanceof TooLong || attempt >= attempts) throw error;
      await new Promise((resolve) => setTimeout(resolve, 3000 * attempt));
    }
  }
};
/** Сами ссылки разбираются в странице, чтобы не тащить в Node разбор HTML. */
const accountLinks = (text) => page.evaluate(`(() => {
  const d = __parse(${JSON.stringify(text)});
  return JSON.stringify([...d.querySelectorAll('a[href]')].filter((a) => /^aoainfo\\.csp\\?/.test(a.getAttribute('href')) && /^RUB\\./.test(__clean(a.textContent)) && ${penaltyPass ? '' : '!'}/\\.Пеня$/.test(__clean(a.textContent))).map((a) => a.getAttribute('href')));
})()`).then((json) => [...new Set(JSON.parse(json))]);
const save = (name, text) => writeFileSync(join(cards, `${name}.html.gz`), gzipSync(text), { mode: 0o600 });
const base = 'https://gkh.nsk.ru/csp/gkhc2/';

let processed = 0;
let accounts = 0;
let journals = 0;
let failed = 0;
const started = Date.now();
for (const id of queue) {
  if (processed >= limit) break;
  try {
    const card = await fetchPage(`${cardBase}&ObjectId=${id}&Mode=select&AutoClose=1`);
    if (card === null) { appendFileSync(doneFile, JSON.stringify({ id, pages: -1 }) + '\n', { mode: 0o600 }); processed += 1; continue; }
    if (!penaltyPass) save(`${id}_card`, card);
    const pages = await accountLinks(card);
    let index = penaltyPass ? 100 : 0;
    const seenJournals = new Set();
    for (const href of pages) {
      index += 1;
      const text = await fetchPage(new URL(href, base).href);
      if (text === null) throw new Error('нет страницы счёта');
      const journal = (text.match(/jsShowAccountJournal\('info','(journal\.csp\?[^']+)'/) || [])[1];
      save(`${id}_${journal ? 'account' : 'contract'}_${index}`, text);
      await new Promise((resolve) => setTimeout(resolve, PAUSE_MS));
      if (!journal) continue;
      accounts += 1;
      const accountKey = journal.match(/account=([^&]+)/)[1];
      if (seenJournals.has(accountKey)) continue;
      seenJournals.add(accountKey);
      try {
        const history = await fetchPage(new URL(`${journal}&${range}`, base).href, LONG_MS, 1);
        if (history === null) throw new Error('нет журнала');
        save(`${id}_journal_${index}`, history);
      } catch (error) {
        if (!(error instanceof TooLong)) throw error;
        // Весь журнал не успел: тот же журнал по годам. Несделанные годы пишутся в список на повтор.
        for (let year = 2000; year <= now.getFullYear(); year += 1) {
          const end = year === now.getFullYear() ? `toyear=${year}&tomonth=${now.getMonth() + 1}&today=${now.getDate()}` : `toyear=${year}&tomonth=12&today=31`;
          try {
            const part = await fetchPage(new URL(`${journal}&fromyear=${year}&frommonth=1&fromday=1&${end}`, base).href, LONG_MS, 1);
            if (part !== null) save(`${id}_journal_${index}_${year}`, part);
          } catch (partError) {
            if (!(partError instanceof TooLong)) throw partError;
            appendFileSync(join(root, 'raw', 'cards.big.log'), `${id} ${index} ${year}\n`, { mode: 0o600 });
          }
        }
      }
      journals += 1;
      await new Promise((resolve) => setTimeout(resolve, PAUSE_MS));
    }
    appendFileSync(doneFile, JSON.stringify({ id, pages: pages.length }) + '\n', { mode: 0o600 });
  } catch (error) {
    failed += 1;
    appendFileSync(join(root, 'raw', 'cards.errors.log'), `${id} ${String(error.message).slice(0, 120)}\n`, { mode: 0o600 });
  }
  processed += 1;
  if (processed % 25 === 0) console.log(`абонентов ${processed}/${queue.length}, журналов ${journals}, ошибок ${failed}, мин ${Math.round((Date.now() - started) / 60000)}`);
}
console.log(`готово: абонентов ${processed}, страниц счетов ${accounts}, журналов ${journals}, ошибок ${failed}`);
page.close();
