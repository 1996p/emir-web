// Разведка: только названия колонок, механика страниц и счётчики. Строк с данными не выводит.
import { connect } from './cdp.mjs';

const page = await connect();
const result = await page.evaluate(`(async () => {
  const anchors = [...document.querySelectorAll('a[href]')].filter((a) => /pmrolesmain/.test(a.getAttribute('href'))).map((a) => [__clean(a.textContent), a.href]);
  const menu = [];
  for (const [section, href] of anchors) {
    const doc = __parse((await __get(href)).text);
    for (const a of doc.querySelectorAll('a[href]')) {
      const name = __clean(a.textContent);
      if (name) menu.push({ section, name, url: new URL(a.getAttribute('href'), location.href).href });
    }
  }
  window.__menu = menu;
  return { loggedIn: !document.querySelector('input[type=password]'), sections: anchors.map((item) => item[0]), items: menu.length };
})()`);
console.log(JSON.stringify(result));

const sample = await page.evaluate(`(async () => {
  const item = window.__menu.find((m) => m.name === 'Абоненты' && /abonentlist/.test(m.url));
  const text = (await __get(item.url)).text;
  const doc = __parse(text);
  const forms = [...doc.forms].map((form) => ({ method: form.method, action: new URL(form.getAttribute('action') || '.', item.url).pathname.split('/').pop(),
    fields: [...form.elements].filter((el) => el.name && !/^CSPToken$/.test(el.name)).map((el) => el.type + ':' + el.name + (el.type === 'hidden' || el.tagName === 'SELECT' ? '=' + String(el.value).slice(0, 16) : '')) }));
  const scripts = [...doc.querySelectorAll('script')].map((s) => s.textContent).join('\\n');
  const fns = [...scripts.matchAll(/function\\s+([A-Za-z_][A-Za-z0-9_]*)\\s*\\(/g)].map((m) => m[1]);
  const open = (scripts.match(/function OpenCSV[\\s\\S]{0,420}/) || [''])[0];
  return { forms, fns: [...new Set(fns)].slice(0, 30), open: open.replace(/CSPToken=[^'"&]+/g, 'CSPToken=…') };
})()`);
console.log(JSON.stringify(sample, null, 1).slice(0, 5000));
page.close();
