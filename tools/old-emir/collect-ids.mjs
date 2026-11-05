// Собирает номера всех абонентов через страницу результатов поиска. Поиск отдаёт не больше 1000 строк,
// поэтому запросы идут по буквам и сочетаниям, пока новые номера не перестанут появляться. В файл пишутся только номера.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { openTab } from './cdp.mjs';

const root = process.argv[2];
const file = join(root, 'raw', 'abonent-ids-search.json');
const items = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const all = new Set(existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : []);
const url = items.find((item) => item.name === 'Поиск' && item.kind === 'zen').url;
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const letters = 'абвгдежзийклмнопрстуфхцчшщыэюя'.split('');
const queue = [...letters, ...'0123456789'.split(''), '"', 'ооо', 'мку', 'мбу', 'мкоу', 'администрац'];
// Выполненные запросы сохраняются, чтобы после перезапуска не повторять их.
const triedFile = join(root, 'raw', 'abonent-ids-tried.json');
const tried = new Set(existsSync(triedFile) ? JSON.parse(readFileSync(triedFile, 'utf8')) : []);
const capped = [];
while (queue.length) {
  const fio = queue.shift();
  if (tried.has(fio)) continue;
  const search = await openTab(url);
  try {
    await wait(3000);
    await search.evaluate(`(() => { zenPage.Search('FIO','Address','',${JSON.stringify(fio)},'','',''); return 1; })()`);
    await wait(2500);
    const src = await search.evaluate(`document.querySelector('iframe').src`);
    const list = await openTab(src);
    try {
      await wait(3000);
      await list.evaluate(`(() => { const t=_zenIndex.filter(Boolean).find(c=>c._type==='tablePane'); t.rowCount='__p'; t.maxRows=500000; t.pageSize=500000; t.executeQuery(false); return 1; })()`);
      let state = '__p';
      for (let i = 0; i < 40 && state === '__p'; i += 1) { await wait(2000); state = await list.evaluate(`String(_zenIndex.filter(Boolean).find(c=>c._type==='tablePane').rowCount)`); }
      await wait(1000);
      const ids = JSON.parse(await list.evaluate(`JSON.stringify([..._zenIndex.filter(Boolean).find(c=>c._type==='tablePane').getEnclosingDiv().querySelectorAll('tr[zenValue]')].map(r=>r.getAttribute('zenValue')))`));
      const before = all.size;
      ids.forEach((id) => all.add(id));
      writeFileSync(file, JSON.stringify([...all]), { mode: 0o600 });
      console.log(`запрос ${fio.length} букв, строк ${ids.length}, новых ${all.size - before}, всего ${all.size}`);
      // Упёрлись в лимит: уточняем запрос следующей буквой.
      tried.add(fio);
      writeFileSync(triedFile, JSON.stringify([...tried]), { mode: 0o600 });
      if (ids.length >= 1000) { capped.push(fio); for (const next of letters) queue.push(fio + next); }
    } finally { await list.close(); }
  } catch (error) {
    console.log('ошибка запроса', String(error.message).slice(0, 80));
  } finally { await search.close(); }
}
console.log(`готово: всего ${all.size}, с лимитом ${capped.length}`);
