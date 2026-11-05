// Подключение к окну Chrome с уже выполненным входом в старый ЕМИР.
// Запросы выполняются внутри страницы, поэтому идут с сессией пользователя. Только чтение.
export async function connect(port = 9222) {
  const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const target = list.find((item) => item.type === 'page' && item.url.startsWith('https://gkh.nsk.ru/') && !item.url.includes('ZENInterface'));
  if (!target) throw new Error('Нет вкладки старого ЕМИР');
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let sequence = 0;
  const waiting = new Map();
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (waiting.has(message.id)) { waiting.get(message.id)(message); waiting.delete(message.id); }
  };
  // Если вкладка закрылась, ожидающие запросы завершаются ошибкой, а не висят до выхода процесса.
  socket.onclose = () => { for (const resolve of waiting.values()) resolve({ result: { exceptionDetails: { exception: { description: 'Вкладка закрыта' } } } }); waiting.clear(); };
  const send = (method, params) => new Promise((resolve) => {
    if (socket.readyState !== 1) { resolve({ result: { exceptionDetails: { exception: { description: 'Вкладка закрыта' } } } }); return; }
    const id = ++sequence; waiting.set(id, resolve); socket.send(JSON.stringify({ id, method, params }));
  });

  /** Выполняет выражение в странице и возвращает значение. */
  const evaluate = async (expression) => {
    const reply = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, timeout: 180000 });
    if (reply.result?.exceptionDetails) throw new Error(reply.result.exceptionDetails.exception?.description ?? 'Ошибка в странице');
    return reply.result?.result?.value;
  };

  await evaluate(`window.__get = async (url) => {
    const response = await fetch(url, { credentials: 'include' });
    const bytes = await response.arrayBuffer();
    const charset = (response.headers.get('content-type') || '').match(/charset=([^;]+)/i);
    return { status: response.status, type: response.headers.get('content-type'), final: response.url, text: new TextDecoder(charset ? charset[1].trim() : 'utf-8').decode(bytes) };
  };
  window.__parse = (text) => new DOMParser().parseFromString(text, 'text/html');
  window.__clean = (value) => (value || '').replace(/\\s+/g, ' ').trim();
  true`);
  return { evaluate, close: () => socket.close(), url: target.url };
}

/** Открывает страницу в отдельной вкладке того же окна; нужна для списков, которые живут только в браузере. */
export async function openTab(url, port = 9222) {
  // Фоновая вкладка: обычное открытие выводит окно поверх всех и забирает экран у пользователя.
  const { webSocketDebuggerUrl: browserUrl } = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  const browser = new WebSocket(browserUrl);
  await new Promise((resolve, reject) => { browser.onopen = resolve; browser.onerror = reject; });
  const targetId = await new Promise((resolve, reject) => {
    browser.onmessage = (event) => {
      const message = JSON.parse(event.data);
      if (message.id === 1) message.result ? resolve(message.result.targetId) : reject(new Error('Не открылась вкладка'));
    };
    browser.send(JSON.stringify({ id: 1, method: 'Target.createTarget', params: { url, background: true } }));
  });
  browser.close();
  const target = { id: targetId };
  const socket = new WebSocket(`ws://127.0.0.1:${port}/devtools/page/${targetId}`);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let sequence = 0;
  const waiting = new Map();
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (waiting.has(message.id)) { waiting.get(message.id)(message); waiting.delete(message.id); }
  };
  // Если вкладка закрылась, ожидающие запросы завершаются ошибкой, а не висят до выхода процесса.
  socket.onclose = () => { for (const resolve of waiting.values()) resolve({ result: { exceptionDetails: { exception: { description: 'Вкладка закрыта' } } } }); waiting.clear(); };
  const send = (method, params) => new Promise((resolve) => {
    if (socket.readyState !== 1) { resolve({ result: { exceptionDetails: { exception: { description: 'Вкладка закрыта' } } } }); return; }
    const id = ++sequence; waiting.set(id, resolve); socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async (expression) => {
    const reply = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, timeout: 300000 });
    if (reply.result?.exceptionDetails) throw new Error(reply.result.exceptionDetails.exception?.description ?? 'Ошибка в странице');
    return reply.result?.result?.value;
  };
  for (let attempt = 0; attempt < 120; attempt += 1) {
    if (await evaluate('document.readyState') === 'complete') break;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  const close = async () => { socket.close(); await fetch(`http://127.0.0.1:${port}/json/close/${target.id}`); };
  return { evaluate, close };
}
