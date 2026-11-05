import { useState, type FormEvent } from 'react';
import { errorText } from '@/api';
import { useSession } from '@/layout/role';
import { Button, Field, Notice, TextInput } from '@/ui';

export function LoginPage() {
  const { signIn } = useSession();
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await signIn(login.trim(), password);
    } catch (reason) {
      setError(errorText(reason));
      setBusy(false);
    }
  };

  return (
    <main style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--tint)', padding: 16 }}>
      <form className="card stack" style={{ width: 360, maxWidth: '100%', padding: 24, gap: 14 }} onSubmit={(event) => void submit(event)}>
        <h1 style={{ fontSize: 22, fontWeight: 700 }}>ЕМИР ЖКХ</h1>
        <Field label="Логин">{(id) => <TextInput id={id} autoFocus autoComplete="username" value={login} onChange={(event) => setLogin(event.target.value)} />}</Field>
        <Field label="Пароль">{(id) => <TextInput id={id} type="password" autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} />}</Field>
        {error && <Notice tone="warn">{error}</Notice>}
        <Button type="submit" variant="primary" size="lg" disabled={busy || !login.trim() || !password}>Войти</Button>
      </form>
    </main>
  );
}
