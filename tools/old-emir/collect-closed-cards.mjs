// Журналы счетов закрытых услуг: в карточке включается «Показывать расторгнутые» (только показ), берутся ссылки на эти счета.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { join } from 'node:path';
import { connect, openTab } from './cdp.mjs';
const root = process.argv[2];
const inventory = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const owners = JSON.parse(readFileSync(join(root, 'raw', 'closed-owners.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const out = join(root, 'raw', 'cards', 'closed');
mkdirSync(out, { recursive: true, mode: 0o700 });
const head = JSON.parse(readFileSync(join(root, 'raw', 'abonentlist.state.json'), 'utf8')).head;
const col = head.indexOf('ЛС');
const sample = readFileSync(join(root, 'raw', 'abonentlist.jsonl'), 'utf8').trim().split('\n').map((l) => JSON.parse(l)).find((row) => /\d/.test(row.cells[col] ?? '')).cells[col].split(/[ ,]/)[0];
const search = await openTab(inventory.find((item) => item.name === 'Поиск' && item.kind === 'zen').url);
await wait(4000);
await search.evaluate(`(() => { zenPage.Search('AccountNumber','Address','','',${JSON.stringify(sample)},'',''); return 1; })()`);
await wait(4000);
const results = await openTab(await search.evaluate(`document.querySelector('iframe').src`));
await wait(5000);
const cardBase = await results.evaluate(`new URL(zenPage.AbonentCardURL, location.href).href`);
await results.close(); await search.close();
// Номера закрытых счетов с остатком и карточки, где их искать: владельцы и карточки без действующих услуг.
const targets = new Set(owners.flatMap(([, , numbers]) => numbers));
const emptyCards = readFileSync(join(root, 'normalized', 'abonents.jsonl'), 'utf8').trim().split('\n').map((l) => JSON.parse(l)).filter((a) => a.services.length === 0).map((a) => a.id);
const cards = [...new Set([...owners.map(([id]) => id), ...emptyCards])];
const page = await connect();
const now = new Date();
const range = `fromyear=2000&frommonth=1&fromday=1&toyear=${now.getFullYear()}&tomonth=${now.getMonth() + 1}&today=${now.getDate()}`;
const found = new Set();
for (const cardId of cards) {
  const card = await openTab(`${cardBase}&ObjectId=${cardId}&Mode=select&AutoClose=1`);
  try {
    await wait(5000);
    await card.evaluate(`(() => { const t = [...document.querySelectorAll('input[type=checkbox]')].find(b => /расторгнут/i.test(b.closest('table')?.textContent || '')); if (t && !t.checked) t.click(); return 1; })()`);
    await wait(5000);
    const links = JSON.parse(await card.evaluate(`JSON.stringify([...document.querySelectorAll('a[href*=aoainfo]')].map(a => [a.textContent.trim(), a.href]).filter(([t]) => /^RUB\\./.test(t)))`));
    for (const [number, href] of links) {
      const core = number.match(/RUB\.(\d+)/)?.[1];
      if (!targets.has(core) || found.has(number)) continue;
      const account = JSON.parse(await page.evaluate(`(async () => { const r = await fetch(${JSON.stringify(href)}, { credentials: 'include' }); return JSON.stringify({ text: await r.text() }); })()`)).text;
      const journal = (account.match(/jsShowAccountJournal\('info','(journal\.csp\?[^']+)'/) || [])[1];
      const safe = number.replace(/[^A-Za-z0-9А-Яа-я.]/g, '_');
      writeFileSync(join(out, `${safe}_card${cardId}_account.html.gz`), gzipSync(account), { mode: 0o600 });
      if (!journal) continue;
      const history = JSON.parse(await page.evaluate(`(async () => { const r = await fetch(${JSON.stringify(new URL(`${journal}&${range}`, 'https://gkh.nsk.ru/csp/gkhc2/').href)}, { credentials: 'include' }); return JSON.stringify({ text: await r.text() }); })()`)).text;
      writeFileSync(join(out, `${safe}_card${cardId}_journal.html.gz`), gzipSync(history), { mode: 0o600 });
      found.add(number);
    }
  } catch (error) {
    console.log('ошибка карточки', String(error.message).slice(0, 60));
  } finally { await card.close(); }
}
console.log('нужно счетов', targets.size, 'найдено журналов', found.size, 'просмотрено карточек', cards.length);
page.close();
