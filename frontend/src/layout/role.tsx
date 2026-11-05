import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, setUnauthorizedHandler } from '@/api';
import type { CurrentUser, Role } from '@/domain/types';

export const ROLE_LABEL: Record<Role, string> = {
  cashier: 'Кассир',
  service: 'Абонентский отдел',
  accountant: 'Бухгалтер',
  admin: 'Администратор',
};

export interface NavItem {
  to: string;
  label: string;
  roles: Role[];
}

/** Скрывает недоступные разделы. Настоящую проверку прав выполняет сервер. */
export const NAV: NavItem[] = [
  { to: '/abonents', label: 'Абоненты', roles: ['cashier', 'service', 'accountant', 'admin'] },
  { to: '/cash', label: 'Касса', roles: ['cashier', 'admin'] },
  { to: '/readings', label: 'Показания', roles: ['cashier', 'service', 'accountant', 'admin'] },
  { to: '/period', label: 'Месяц', roles: ['service', 'accountant', 'admin'] },
  { to: '/corrections', label: 'Корректировки', roles: ['cashier', 'accountant', 'admin'] },
  { to: '/reports', label: 'Отчёты', roles: ['cashier', 'service', 'accountant', 'admin'] },
  { to: '/documents', label: 'Документы', roles: ['cashier', 'service', 'accountant', 'admin'] },
  { to: '/checks', label: 'Сверки', roles: ['accountant', 'admin'] },
  { to: '/exchange', label: 'Обмен', roles: ['accountant', 'admin'] },
  { to: '/directories', label: 'Справочники', roles: ['service', 'accountant', 'admin'] },
  { to: '/admin', label: 'Админ', roles: ['admin'] },
];

interface SessionValue {
  /** undefined означает, что сессия ещё проверяется */
  user: CurrentUser | null | undefined;
  signIn: (login: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
  refresh: () => Promise<void>;
}

const SessionContext = createContext<SessionValue | null>(null);

export function RoleProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null | undefined>(undefined);

  useEffect(() => {
    setUnauthorizedHandler(() => setUser(null));
    api.auth.me().then(setUser, () => setUser(null));
  }, []);

  const signIn = useCallback(async (login: string, password: string) => {
    setUser(await api.auth.login(login, password));
  }, []);

  const signOut = useCallback(async () => {
    await api.auth.logout();
    setUser(null);
  }, []);

  const refresh = useCallback(async () => {
    setUser(await api.auth.me());
  }, []);

  const value = useMemo(() => ({ user, signIn, signOut, refresh }), [user, signIn, signOut, refresh]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionValue {
  const value = useContext(SessionContext);
  if (!value) throw new Error('useSession вызван вне RoleProvider');
  return value;
}

/** Роль вошедшего пользователя. Вызывается только на экранах внутри AppShell. */
export function useRole(): Role {
  const { user } = useSession();
  if (!user) throw new Error('Пользователь не вошёл');
  return user.role;
}
