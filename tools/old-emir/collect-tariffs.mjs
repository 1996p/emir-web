// Список тарифов по каждой услуге: страница выбора услуги отдаёт ссылку на таблицу тарифов этой услуги.
// Только GET, ничего не сохраняется в старой системе. Строки пишутся в raw/tariffs.jsonl.
import { writeFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { connect } from './cdp.mjs';

const root = process.argv[2];
const items = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const item = items.find((entry) => entry.path.includes('Contract/Tarif/TarifList'));
const page = await connect();
const result = JSON.parse(await page.evaluate(`(async () => {
  const base = ${JSON.stringify(item.url)};
  const start = __parse((await __get(base)).text);
  const token = start.querySelector('[name=CSPToken]').value;
  const top = start.querySelector('[name=topId]').value;
  const services = [...start.querySelectorAll('select[name=Service] option')].filter((o) => o.value).map((o) => [o.value, __clean(o.textContent)]);
  const out = [];
  const pages = {};
  for (const [id, name] of services) {
    const u = new URL(base); u.search = ''; u.searchParams.set('CSPToken', token); u.searchParams.set('topId', top); u.searchParams.set('Service', id);
    const frame = __parse((await __get(u.href)).text).querySelector('iframe');
    if (!frame) { pages[name] = 'нет таблицы'; continue; }
    const listUrl = new URL(frame.getAttribute('src'), u.href).href;
    let page = 1, total = 1, head = [];
    do {
      const d = __parse((await __get(listUrl + '&Query_TarifTarifList_ONPAGE=300&Query_TarifTarifList_PAGE=' + page)).text);
      total = Number(((d.documentElement.innerHTML.match(/Выберите страницу \\(1 - (\\d+)\\)/) || [])[1]) || 1);
      const table = [...d.querySelectorAll('table')].sort((a, b) => b.rows.length - a.rows.length)[0];
      for (const row of table ? table.rows : []) {
        const cells = [...row.cells].map((c) => __clean(c.textContent));
        if (row.querySelector('th') || row.querySelector('a[href*=_ORDER]')) { if (!head.length) head = cells; continue; }
        if (cells.length >= 6) out.push({ serviceId: id, service: name, cells });
      }
      page += 1;
    } while (page <= total);
    pages[name] = total;
    out.head = head;
  }
  return JSON.stringify({ rows: out, head: out.head, pages });
})()`));
writeFileSync(join(root, 'raw', 'tariffs.jsonl'), result.rows.map((row) => JSON.stringify(row)).join('\n') + '\n', { mode: 0o600 });
writeFileSync(join(root, 'raw', 'tariffs.state.json'), JSON.stringify({ head: result.head, pages: result.pages, rows: result.rows.length }), { mode: 0o600 });
console.log('тарифов', result.rows.length, 'шапка', JSON.stringify(result.head), 'страниц по услугам', JSON.stringify(result.pages));
page.close();
