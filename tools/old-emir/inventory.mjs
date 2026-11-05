// Опись меню: вид страницы, колонки и объём. Страницы с действиями не открываются, строки с данными не выводятся.
import { appendFileSync, writeFileSync } from 'node:fs';
import { connect } from './cdp.mjs';

const output = process.argv[2];
const page = await connect();
const items = JSON.parse(await page.evaluate(`(async () => {
  const anchors = [...document.querySelectorAll('a[href]')].filter((a) => /pmrolesmain/.test(a.getAttribute('href'))).map((a) => [__clean(a.textContent), a.href]);
  const items = new Map();
  for (const [section, href] of anchors) {
    const doc = __parse((await __get(href)).text);
    for (const a of doc.querySelectorAll('a[href]')) {
      const name = __clean(a.textContent);
      const url = new URL(a.getAttribute('href'), location.href);
      if (name && url.pathname.startsWith('/csp/gkhc2/')) items.set(section + '|' + name, { section, name, url: url.href, path: url.pathname.replace('/csp/gkhc2/', '') });
    }
  }
  return JSON.stringify([...items.values()]);
})()`));

const deny = /logout|chpass|delete|remove|upload|save|exec|import|close|cancel|load|attach|Message|ReClose|Charge(1|Back)?\.cls|MoneyBack|MoneyTransfer|Claim|RestCorrect|PaymentCancel|Register|CreateGeneral|ChangeAddress|InsertReading|ChangeTarif|CheckCharges|AllowCharge|Setting|HeatSeason|YearHeat|daemon|CounterEdit|CashOrderBack|pmrolesmain|index\.csp/i;
writeFileSync(output, '');
console.log('пунктов', items.length);
for (const item of items) {
  const row = { ...item };
  if (deny.test(item.path)) { row.kind = 'skip'; appendFileSync(output, JSON.stringify(row) + '\n'); continue; }
  const started = Date.now();
  const info = await Promise.race([
    page.evaluate(`(async () => {
      const fio = /[А-ЯЁ][а-яё]+\\s+[А-ЯЁ][а-яё]+\\s+[А-ЯЁ][а-яё]+/;
      const reply = await __get(${JSON.stringify(item.url)});
      const doc = __parse(reply.text);
      const input = doc.querySelector('input[name$=_ONPAGE]');
      const query = input ? input.name.replace(/_ONPAGE$/, '') : null;
      const zen = /zenCreatePageModel|ZEN_Component/.test(reply.text);
      const tables = [...doc.querySelectorAll('table')];
      const head = tables.map((t) => [...t.querySelectorAll('th')].map((c) => __clean(c.textContent)).filter(Boolean)).sort((a, b) => b.length - a.length)[0] || [];
      return JSON.stringify({ kind: query ? 'query' : zen ? 'zen' : 'other', query, status: reply.status, bytes: reply.text.length, onPage: input ? Number(input.value || 0) : 0,
        pages: Number((reply.text.match(/Выберите страницу \\(1 - (\\d+)\\)/) || [])[1] || 0), rowsOnPage: tables.reduce((max, t) => Math.max(max, t.rows.length), 0), head: head.filter((h) => !fio.test(h)).slice(0, 40) });
    })()`),
    new Promise((resolve) => setTimeout(() => resolve('{"kind":"slow"}'), 60000)),
  ]);
  Object.assign(row, JSON.parse(info), { ms: Date.now() - started });
  appendFileSync(output, JSON.stringify(row) + '\n');
  await new Promise((resolve) => setTimeout(resolve, 200));
}
console.log('готово');
page.close();
