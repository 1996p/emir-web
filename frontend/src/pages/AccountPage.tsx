import { useState, type FormEvent } from 'react';
import { api, errorText } from '@/api';
import { ROLE_LABEL, useSession } from '@/layout/role';
import { Button, Field, Notice, TextInput } from '@/ui';

export function ChangePasswordForm({ forced }: { forced?: boolean }) {
  const { refresh } = useSession();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const mismatch = repeat !== '' && next !== repeat;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    setDone(false);
    try {
      await api.auth.changePassword(current, next);
      setCurrent('');
      setNext('');
      setRepeat('');
      setDone(true);
      await refresh();
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  return (
    <form className="stack" style={{ gap: 12 }} onSubmit={(event) => void submit(event)}>
      <Field label={forced ? 'Временный пароль' : 'Текущий пароль'}>{(id) => <TextInput id={id} type="password" autoFocus autoComplete="current-password" value={current} onChange={(event) => setCurrent(event.target.value)} />}</Field>
      <Field label="Новый пароль" hint="Не короче 12 символов и без логина.">{(id) => <TextInput id={id} type="password" autoComplete="new-password" value={next} onChange={(event) => setNext(event.target.value)} />}</Field>
      <Field label="Новый пароль ещё раз">{(id) => <TextInput id={id} type="password" autoComplete="new-password" value={repeat} onChange={(event) => setRepeat(event.target.value)} />}</Field>
      {mismatch && <Notice tone="warn">Пароли не совпадают.</Notice>}
      {error && <Notice tone="warn">{error}</Notice>}
      {done && <Notice tone="ok">Пароль изменён. На других устройствах нужно войти заново.</Notice>}
      <Button type="submit" variant="primary" size="lg" disabled={!current || !next || next !== repeat}>Сменить пароль</Button>
    </form>
  );
}

/** Первый вход и вход после сброса: до смены пароля сервер не пускает никуда, кроме этого экрана. */
export function ForcedPasswordChange() {
  const { user, signOut } = useSession();
  return (
    <main style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--tint)', padding: 16 }}>
      <div className="card stack" style={{ width: 400, maxWidth: '100%', padding: 24, gap: 14 }}>
        <h1 style={{ fontSize: 22, fontWeight: 700 }}>Смените временный пароль</h1>
        <p className="muted" style={{ lineHeight: 1.5 }}>{user?.displayName}, этот пароль вам выдал администратор. Придумайте свой, его никто не будет знать.</p>
        <ChangePasswordForm forced />
        <Button size="full" onClick={() => void signOut()}>Выйти</Button>
      </div>
    </main>
  );
}

export function AccountPage() {
  const { user } = useSession();
  return (
    <main className="main">
      <div className="scroll pad">
        <div className="card stack" style={{ width: 440, maxWidth: '100%', padding: 20, gap: 14 }}>
          <h1 className="title">Мой профиль</h1>
          <p className="muted">{user?.displayName}, {user ? ROLE_LABEL[user.role].toLowerCase() : ''}. Логин: <span className="mono">{user?.login}</span></p>
          <h2 className="section-title">Смена пароля</h2>
          <ChangePasswordForm />
        </div>
      </div>
    </main>
  );
}
