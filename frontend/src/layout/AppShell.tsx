import { useEffect, useState } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';
import { api, useApi } from '@/api';
import { ForcedPasswordChange } from '@/pages/AccountPage';
import { LoginPage } from '@/pages/LoginPage';
import { CommandPalette } from './CommandPalette';
import { NAV, ROLE_LABEL, useSession } from './role';

export function AppShell() {
  const { user } = useSession();
  if (user === undefined) return null;
  if (user === null) return <LoginPage />;
  if (user.mustChangePassword) return <ForcedPasswordChange />;
  return <SignedInShell />;
}

function SignedInShell() {
  const { user, signOut } = useSession();
  const location = useLocation();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const { data: today } = useApi(() => api.today.get(), []);
  const role = user!.role;

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setPaletteOpen((open) => !open);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const section = NAV.find((item) => location.pathname === item.to || location.pathname.startsWith(`${item.to}/`));
  const allowed = !section || section.roles.includes(role);

  return (
    <div className="shell">
      <header className="topbar">
        <Link to="/" className="topbar-logo">ЕМИР ЖКХ</Link>
        <nav className="topbar-nav" aria-label="Разделы">
          {NAV.filter((item) => item.roles.includes(role)).map((item) => (
            <NavLink key={item.to} to={item.to}>{item.label}</NavLink>
          ))}
        </nav>
        <div className="topbar-side">
          <button type="button" className="topbar-search" onClick={() => setPaletteOpen(true)}>
            <span>Поиск</span>
            <span className="mono small">Ctrl K</span>
          </button>
          <Link to="/" className="topbar-link">
            Сообщения {today && today.messages.length > 0 && <span className="counter">{today.messages.length}</span>}
          </Link>
          <Link to="/account" className="topbar-link" title={`${user!.displayName}: мой профиль`}>{ROLE_LABEL[role]}</Link>
          <button type="button" className="topbar-link" style={{ background: 'transparent', border: 0 }} onClick={() => void signOut()}>Выйти</button>
        </div>
      </header>
      <div className="shell-body">
        {allowed ? <Outlet /> : <main className="main muted" style={{ alignItems: 'center', justifyContent: 'center', fontSize: 15 }}>Этот раздел недоступен для вашей роли.</main>}
      </div>
      {paletteOpen && <CommandPalette onClose={() => setPaletteOpen(false)} />}
    </div>
  );
}
