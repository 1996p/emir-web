import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { render } from '@testing-library/react';
import { vi } from 'vitest';
import { RoleProvider, useSession } from '@/layout/role';
import type { Role } from '@/domain/types';

/** Ответ маршрута: голые данные (→200), либо { status, data } для ошибок/204. */
type RouteValue = unknown | { status: number; data?: unknown };
export type Routes = Record<string, RouteValue | ((body: unknown, query: URLSearchParams) => RouteValue)>;

export interface MockFetch {
  calls: Array<{ method: string; path: string; body: unknown }>;
  /** Подменить/добавить маршрут уже после рендера (например, ответ на POST). */
  set(key: string, value: RouteValue | ((body: unknown) => RouteValue)): void;
}

/** Ставит global.fetch, который отвечает по карте «МЕТОД /путь». Query в ключ не входит, но передаётся функции-ответу вторым аргументом. */
export function installFetch(routes: Routes, role: Role | null = 'admin'): MockFetch {
  const table: Routes = {
    'GET /auth/me': role ? { login: 'test', displayName: 'Тест', role, mustChangePassword: false } : { status: 401, data: { detail: 'нет сессии' } },
    ...routes,
  };
  const calls: MockFetch['calls'] = [];

  const mock = vi.fn(async (url: string, init?: RequestInit) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    const path = url.replace(/^\/api/, '').split('?')[0] ?? '';
    // Тело запроса: JSON-строка разбирается, FormData (загрузка файла) помечается как multipart
    const raw_body = init?.body;
    const body = typeof raw_body === 'string' ? JSON.parse(raw_body) : raw_body instanceof FormData ? { multipart: true } : undefined;
    calls.push({ method, path, body });

    const raw = table[`${method} ${path}`];
    if (raw === undefined) return new Response(JSON.stringify({ detail: `нет мока: ${method} ${path}` }), { status: 404, headers: { 'Content-Type': 'application/json' } });
    const resolved = typeof raw === 'function' ? (raw as (b: unknown, query: URLSearchParams) => RouteValue)(body, new URLSearchParams(url.split('?')[1] ?? '')) : raw;
    const envelope = (resolved && typeof resolved === 'object' && 'status' in (resolved as object)) ? resolved as { status: number; data?: unknown } : { status: 200, data: resolved };
    if (envelope.status === 204) return new Response(null, { status: 204 });
    return new Response(JSON.stringify(envelope.data ?? null), { status: envelope.status, headers: { 'Content-Type': 'application/json' } });
  });

  vi.stubGlobal('fetch', mock);
  return { calls, set: (key, value) => { table[key] = value; } };
}

/** Рендерит children только когда сессия загрузилась — как AppShell в реальном приложении. */
function SessionGate({ children }: { children: ReactNode }) {
  const { user } = useSession();
  if (user === undefined) return <div data-testid="session-loading">загрузка</div>;
  if (user === null) return <div data-testid="unauth">не вошёл</div>;
  return <>{children}</>;
}

export function renderPage(ui: ReactNode, path = '/') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <RoleProvider>
        <SessionGate>{ui}</SessionGate>
      </RoleProvider>
    </MemoryRouter>,
  );
}

/** Рендер без «ворот» сессии — для экранов входа, которые показываются неавторизованному. */
export function renderRaw(ui: ReactNode, path = '/') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <RoleProvider>{ui}</RoleProvider>
    </MemoryRouter>,
  );
}
