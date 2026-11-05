// Разведка выгрузки и листания на примере списка абонентов. Выводит только счётчики, размеры и хеши.
import { connect } from './cdp.mjs';

const page = await connect();
const result = await page.evaluate(`(async () => {
  const anchors = [...document.querySelectorAll('a[href]')].filter((a) => /pmrolesmain/.test(a.getAttribute('href'))).map((a) => a.href);
  let url = null;
  for (const href of anchors) {
    const doc = __parse((await __get(href)).text);
    const found = [...doc.querySelectorAll('a[href]')].find((a) => __clean(a.textContent) === 'Абоненты' && /abonentlist/.test(a.getAttribute('href')));
    if (found) { url = new URL(found.getAttribute('href'), location.href).href; break; }
  }
  const hash = async (text) => [...new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text)))].slice(0, 4).map((b) => b.toString(16).padStart(2, '0')).join('');
  const describe = async (label, target) => {
    const reply = await __get(target);
    const doc = __parse(reply.text);
    const tables = [...doc.querySelectorAll('table')].sort((a, b) => b.rows.length - a.rows.length);
    const table = tables[0];
    const firstData = table && table.rows[1] ? __clean(table.rows[1].textContent) : '';
    const pages = (reply.text.match(/Выберите страницу \\\\(1 - (\\\\d+)\\\\)/) || [])[1] || (reply.text.match(/Выберите страницу[^<"']{0,24}/) || [''])[0];
    const isHtml = /<html|<table/i.test(reply.text.slice(0, 3000));
    return { label, status: reply.status, type: reply.type, bytes: reply.text.length, isHtml, lines: reply.text.split('\\n').length, rows: table ? table.rows.length : 0, pages, firstRowHash: await hash(firstData),
      head: isHtml ? null : reply.text.split('\\n')[0].slice(0, 400) };
  };
  const out = [];
  out.push(await describe('обычный', url));
  out.push(await describe('стр 2', url + '&Query_AbonentList_PAGE=2'));
  out.push(await describe('по 2000', url + '&Query_AbonentList_ONPAGE=2000'));
  out.push(await describe('CSV', url + '&CSV=CSV'));
  out.push(await describe('EXCEL', url + '&EXCEL=EXCEL'));
  out.push(await describe('EXCELHTML', url + '&EXCELHTML=1'));
  const doc = __parse((await __get(url)).text);
  const pager = [...doc.querySelectorAll('input[name*=Query_],select[name*=Query_]')].map((el) => el.tagName + ':' + el.type + ':' + el.name + '=' + String(el.value).slice(0, 12) + ' onclick=' + String(el.getAttribute('onclick') || el.getAttribute('onchange') || '').replace(/CSPToken=[^'"&]+/g, 'T').slice(0, 120));
  const csvDr = doc.querySelector('[name=CSVDR]');
  return { out, pager, csvDrClick: String(csvDr ? csvDr.getAttribute('onclick') : '').replace(/CSPToken=[^'"&]+/g, 'T').slice(0, 220) };
})()`);
console.log(JSON.stringify(result, null, 1).slice(0, 6000));
page.close();
