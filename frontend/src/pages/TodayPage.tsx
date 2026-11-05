import { Link } from 'react-router-dom';
import { api, useApi } from '@/api';
import { Notice } from '@/ui';

export function TodayPage() {
  const { data: month } = useApi(() => api.period.current(), []);
  const { data: today, error } = useApi(() => api.today.get(), []);
  const { data: steps } = useApi(() => api.period.steps(), []);
  const next = steps?.find((step) => step.state === 'current');

  return (
    <div className="scroll" style={{ background: 'var(--tint)', padding: 16 }}>
      <div className="stack" style={{ maxWidth: 1320, margin: '0 auto' }}>
        <h1 style={{ fontSize: 24, fontWeight: 700 }}>Сегодня</h1>
        {error && <Notice tone="warn">{error}</Notice>}

        <Link to="/period" className="card" style={{ display: 'block', color: 'var(--ink)', textDecoration: 'none' }}>
          <div className="between" style={{ marginBottom: 10, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 16, fontWeight: 700 }}>{month?.title ?? ''}</span>
            <span className="small muted">{next ? `Следующий шаг: ${next.name.toLowerCase()}` : 'Все шаги выполнены'}</span>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(6, minmax(0, 1fr))', gap: 4 }}>
            {(steps ?? []).map((step) => (
              <div key={step.id}>
                <div style={{ height: 6, borderRadius: 3, background: step.state === 'next' ? 'var(--line)' : step.state === 'done' ? 'var(--ok)' : 'var(--brand)' }} />
                <div className="muted" style={{ fontSize: 12, marginTop: 5 }}>{step.name}</div>
              </div>
            ))}
          </div>
        </Link>

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(340px, 1fr))', gap: 12, alignItems: 'start' }}>
          <section className="card">
            <h2 className="section-title" style={{ marginBottom: 6 }}>Нужно сделать</h2>
            {today?.tasks.map((task) => (
              <Link key={task.title} to={task.to} style={{ display: 'flex', gap: 12, justifyContent: 'space-between', alignItems: 'center', padding: '10px 0', borderTop: '1px solid var(--line-soft)', color: 'var(--ink)', textDecoration: 'none', minHeight: 44 }}>
                <span>
                  <span style={{ display: 'block', fontWeight: 500 }}>{task.title}</span>
                  <span className="small muted" style={{ display: 'block', marginTop: 1 }}>{task.details}</span>
                </span>
                <span className={`mono nowrap${task.warn ? ' warn' : ''}`} style={{ fontSize: 15, fontWeight: 500 }}>{task.value}</span>
              </Link>
            ))}
          </section>

          <section className="card">
            <h2 className="section-title" style={{ marginBottom: 10 }}>Частые действия</h2>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 6 }}>
              {today?.actions.map((action) => (
                <Link key={action.label} to={action.to} className="btn btn-full" style={{ height: 52 }}>{action.label}</Link>
              ))}
            </div>
          </section>

          <section className="card">
            <h2 className="section-title" style={{ marginBottom: 6 }}>Сообщения</h2>
            {today?.messages.map((message) => (
              <div key={message.title} style={{ padding: '10px 0', borderTop: '1px solid var(--line-soft)' }}>
                <div style={{ fontWeight: 500 }}>{message.title}</div>
                <div className="small muted" style={{ marginTop: 1 }}>{message.details}</div>
              </div>
            ))}
            {today?.messages.length === 0 && <p className="muted">Новых сообщений нет.</p>}
          </section>
        </div>
      </div>
    </div>
  );
}
