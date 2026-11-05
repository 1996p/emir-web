import { useEffect, useState } from 'react';
import { api, errorText, useApi } from '@/api';
import type { ManagedUser, Role } from '@/domain/types';
import { ROLE_LABEL } from '@/layout/role';
import { Button, DataTable, Field, Notice, SelectInput, TextInput, type Column } from '@/ui';

const ROLES = Object.keys(ROLE_LABEL) as Role[];

const stateOf = (user: ManagedUser) => {
  if (!user.isActive) return <span className="warn strong">Отключён</span>;
  if (user.locked) return <span className="warn strong">Вход заблокирован</span>;
  if (user.mustChangePassword) return 'Ждёт смены пароля';
  return 'Работает';
};

const COLUMNS: Column<ManagedUser>[] = [
  { key: 'name', header: 'Сотрудник', render: (user) => <span className="strong">{user.displayName}{user.isSelf ? ' (вы)' : ''}</span> },
  { key: 'login', header: 'Логин', mono: true, render: (user) => user.login },
  { key: 'role', header: 'Роль', render: (user) => ROLE_LABEL[user.role] },
  { key: 'state', header: 'Состояние', render: stateOf },
  { key: 'last', header: 'Последний вход', render: (user) => user.lastLogin ?? 'Не входил' },
];

/** Временный пароль виден один раз: сервер его не хранит. */
function TemporaryPassword({ login, password, onClose }: { login: string; password: string; onClose: () => void }) {
  return (
    <Notice tone="warn">
      <strong style={{ display: 'block', marginBottom: 4 }}>Временный пароль для {login}</strong>
      <span className="mono" style={{ display: 'block', fontSize: 16, userSelect: 'all', overflowWrap: 'anywhere' }}>{password}</span>
      <span style={{ display: 'block', margin: '6px 0' }}>Передайте его сотруднику. Больше он нигде не показывается, при первом входе его нужно будет сменить.</span>
      <Button size="sm" onClick={onClose}>Закрыть</Button>
    </Notice>
  );
}

export function UsersPanel() {
  const { data: users, error: loadError } = useApi(() => api.admin.users(), []);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [creating, setCreating] = useState(false);
  const [issued, setIssued] = useState<{ login: string; temporaryPassword: string } | null>(null);
  const selected = users?.find((user) => user.id === selectedId) ?? null;

  return (
    <div className="split">
      <main className="main">
        <div className="bar" style={{ justifyContent: 'space-between' }}>
          <div>
            <h1 className="title">Сотрудники</h1>
            <p className="muted" style={{ fontSize: 13.5, marginTop: 2 }}>Учётные записи и доступ. Сотрудников не удаляем: их имена остаются в журнале.</p>
          </div>
          <Button variant="primary" style={{ width: 170 }} size="auto" onClick={() => { setCreating(true); setSelectedId(null); setIssued(null); }}>Новый сотрудник</Button>
        </div>
        {loadError && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{loadError}</Notice></div>}
        {issued && <div style={{ padding: '8px 16px' }}><TemporaryPassword login={issued.login} password={issued.temporaryPassword} onClose={() => setIssued(null)} /></div>}
        <DataTable columns={COLUMNS} rows={users ?? []} rowKey={(user) => String(user.id)} onRowClick={(user) => { setSelectedId(user.id); setCreating(false); }} isSelected={(user) => user.id === selectedId} />
      </main>
      {creating && <CreatePanel onClose={() => setCreating(false)} onIssued={(result) => { setIssued(result); setCreating(false); }} />}
      {selected && !creating && <EditPanel key={selected.id} user={selected} onIssued={setIssued} />}
    </div>
  );
}

function CreatePanel({ onClose, onIssued }: { onClose: () => void; onIssued: (result: { login: string; temporaryPassword: string }) => void }) {
  const [login, setLogin] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [role, setRole] = useState<Role>('cashier');
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    setError(null);
    try {
      onIssued(await api.admin.createUser({ login, displayName, role }));
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  return (
    <aside className="pane pane-right" aria-label="Новый сотрудник" style={{ width: 340 }}>
      <div className="scroll stack pad">
        <h2 style={{ fontSize: 17, fontWeight: 600 }}>Новый сотрудник</h2>
        <Field label="Имя">{(id) => <TextInput id={id} autoFocus value={displayName} onChange={(event) => setDisplayName(event.target.value)} />}</Field>
        <Field label="Логин" hint="Латинские буквы, цифры, точка, дефис.">{(id) => <TextInput id={id} autoComplete="off" value={login} onChange={(event) => setLogin(event.target.value)} />}</Field>
        <Field label="Роль">{(id) => <SelectInput id={id} value={role} onChange={(event) => setRole(event.target.value as Role)}>{ROLES.map((item) => <option key={item} value={item}>{ROLE_LABEL[item]}</option>)}</SelectInput>}</Field>
        {error && <Notice tone="warn">{error}</Notice>}
        <Button variant="primary" size="full" disabled={!login.trim() || !displayName.trim()} onClick={() => void submit()}>Создать и выдать пароль</Button>
        <Button size="full" onClick={onClose}>Отмена</Button>
      </div>
    </aside>
  );
}

function EditPanel({ user, onIssued }: { user: ManagedUser; onIssued: (result: { login: string; temporaryPassword: string }) => void }) {
  const [displayName, setDisplayName] = useState(user.displayName);
  const [role, setRole] = useState<Role>(user.role);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [confirmingDisable, setConfirmingDisable] = useState(false);

  useEffect(() => { setDisplayName(user.displayName); setRole(user.role); }, [user.displayName, user.role]);

  const run = async (action: () => Promise<unknown>, done: string) => {
    setError(null);
    setNote(null);
    try {
      await action();
      setNote(done);
    } catch (reason) {
      setError(errorText(reason));
    }
    setConfirmingDisable(false);
  };

  return (
    <aside className="pane pane-right" aria-label="Сотрудник" style={{ width: 340 }}>
      <div className="scroll stack pad">
        <h2 style={{ fontSize: 17, fontWeight: 600 }}>{user.displayName}</h2>
        <p className="muted mono">{user.login}</p>
        <Field label="Имя">{(id) => <TextInput id={id} value={displayName} onChange={(event) => setDisplayName(event.target.value)} />}</Field>
        <Field label="Роль" hint={user.isSelf ? 'Свою роль менять нельзя.' : 'Начнёт действовать сразу, без повторного входа.'}>{(id) => <SelectInput id={id} disabled={user.isSelf} value={role} onChange={(event) => setRole(event.target.value as Role)}>{ROLES.map((item) => <option key={item} value={item}>{ROLE_LABEL[item]}</option>)}</SelectInput>}</Field>
        <Button variant="primary" size="full" disabled={!displayName.trim() || (displayName === user.displayName && role === user.role)} onClick={() => void run(() => api.admin.updateUser(user.id, { displayName, role }), 'Сохранено.')}>Сохранить</Button>
        {error && <Notice tone="warn">{error}</Notice>}
        {note && <Notice tone="ok">{note}</Notice>}
        {!user.isSelf && (
          <>
            <h3 className="section-title" style={{ marginTop: 6 }}>Доступ</h3>
            <Button size="full" onClick={() => void run(async () => onIssued(await api.admin.resetPassword(user.id)), 'Выдан временный пароль, прежние сессии завершены.')}>Сбросить пароль</Button>
            {user.locked && <Button size="full" onClick={() => void run(() => api.admin.unlock(user.id), 'Блокировка снята.')}>Снять блокировку входа</Button>}
            {user.isActive ? (
              confirmingDisable ? (
                <>
                  <Notice tone="warn">Сотрудник сразу потеряет доступ, даже если сейчас работает.</Notice>
                  <Button variant="danger" size="full" onClick={() => void run(() => api.admin.setActive(user.id, false), 'Сотрудник отключён.')}>Да, отключить</Button>
                  <Button size="full" onClick={() => setConfirmingDisable(false)}>Отмена</Button>
                </>
              ) : <Button size="full" onClick={() => setConfirmingDisable(true)}>Отключить</Button>
            ) : <Button size="full" onClick={() => void run(() => api.admin.setActive(user.id, true), 'Сотрудник включён.')}>Включить</Button>}
          </>
        )}
      </div>
    </aside>
  );
}
