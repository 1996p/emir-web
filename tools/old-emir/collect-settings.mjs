// Скачивает страницы настроек старой системы одним GET-запросом, без нажатий и отправки форм.
// Страница сохраняется как есть; в консоль идут только подписи полей и таблиц.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { join } from 'node:path';
import { connect } from './cdp.mjs';

const root = process.argv[2];
const names = process.argv.slice(3);
const out = join(root, 'raw', 'settings');
mkdirSync(out, { recursive: true, mode: 0o700 });
const items = readFileSync(join(root, 'inventory.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
const page = await connect();
for (const name of names) {
  const item = items.find((entry) => entry.name === name);
  if (!item) { console.log(name, '| нет в меню'); continue; }
  const reply = JSON.parse(await page.evaluate(`(async () => {
    const r = await __get(${JSON.stringify(item.url)});
    const d = __parse(r.text);
    const label = (s) => { s = __clean(s); return s.length > 60 ? '…' : s.replace(/\\d/g, '9'); };
    const fields = [...d.querySelectorAll('input,select,textarea')].filter((e) => e.name && e.name !== 'CSPToken' && e.type !== 'hidden').map((e) => e.type + ':' + e.name).slice(0, 30);
    const rows = [...d.querySelectorAll('tr')].map((tr) => [...tr.cells].map((c) => label(c.textContent)).filter(Boolean).join(' | ')).filter(Boolean).slice(0, 30);
    const zen = /zenCreatePageModel|ZEN_Component/.test(r.text);
    return JSON.stringify({ status: r.status, bytes: r.text.length, zen, fields, rows, text: r.text });
  })()`));
  const slug = item.path.replace(/\.(csp|cls)$/, '').replace(/[^A-Za-z0-9]+/g, '_');
  writeFileSync(join(out, `${slug}.html.gz`), gzipSync(reply.text), { mode: 0o600 });
  console.log(`== ${name} | ${item.path} | ${reply.status} | ${reply.bytes} байт | zen ${reply.zen}`);
  console.log('   поля:', reply.fields.join(', ').slice(0, 400));
  console.log('   строки:', reply.rows.join(' ## ').slice(0, 900));
}
page.close();
