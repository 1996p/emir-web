import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { formatMoney } from '@/domain/billing';
import type { ChargeIssue, PeriodStep, ServicePreview } from '@/domain/types';
import { useRole } from '@/layout/role';
import { Button, DataTable, DefinitionList, Field, Notice, Tabs, TextInput, type Column } from '@/ui';
import { ReclosePanel } from './ReclosePanel';

type Panel = 'step' | 'settings' | 'closed';
type Tab = 'issues' | 'preview';

const previewColumns = (month: string, showAlready: boolean): Column<ServicePreview>[] => [
  { key: 'service', header: 'Услуга', render: (row) => <span className="strong">{row.service}</span> },
  { key: 'abonents', header: 'Абонентов', align: 'right', mono: true, render: (row) => row.abonents.toLocaleString('ru-RU') },
  { key: 'previous', header: 'Прошлый месяц, ₽', align: 'right', mono: true, render: (row) => formatMoney(row.previous) },
  ...(showAlready ? [{ key: 'already', header: 'Уже начислено, ₽', align: 'right' as const, mono: true, render: (row: ServicePreview) => row.already === 0 ? '—' : formatMoney(row.already) }] : []),
  { key: 'current', header: `${month} всего, ₽`, align: 'right', mono: true, render: (row) => formatMoney(row.current) },
];

const ISSUE_COLUMNS: Column<ChargeIssue>[] = [
  { key: 'abonent', header: 'Абонент', render: (row) => row.abonent },
  { key: 'service', header: 'Услуга', render: (row) => row.service },
  { key: 'problem', header: 'Почему не начислено', render: (row) => <span className="warn strong">{row.problem}</span> },
];

function ChargePreviewPanel({ month }: { month: string }) {
  const { data: preview, error } = useApi(() => api.period.preview(), []);
  if (error) return <div className="pad"><Notice tone="warn">{error}</Notice></div>;
  if (!preview) return null;
  const toAdd = preview.total - preview.already;
  return (
    <div className="scroll">
      <div className="pad"><Notice>
        Расчёт без записи в базу. За {month} всего получится <strong>{formatMoney(preview.total)} ₽</strong>, абонентов: {preview.abonents}, строк: {preview.lines}.
        {preview.already > 0 && <> Уже проведено (счётчики, прошлый запуск): <strong>{formatMoney(preview.already)} ₽</strong>. Проведение {toAdd >= 0 ? 'добавит к долгу' : 'уменьшит долг на'} <strong>{formatMoney(Math.abs(toAdd))} ₽</strong>.</>}
      </Notice></div>
      <DataTable columns={previewColumns(month, preview.already > 0)} rows={preview.services} rowKey={(row) => row.service} />
      {preview.issues.length > 0 && (
        <>
          <h2 className="section-title pad">Не будет начислено · {preview.issues.length}</h2>
          <DataTable columns={ISSUE_COLUMNS} rows={preview.issues} rowKey={(row) => `${row.abonent}-${row.service}`} />
        </>
      )}
    </div>
  );
}

export function PeriodPage() {
  const { data: steps, error: loadError } = useApi(() => api.period.steps(), []);
  const { data: settings } = useApi(() => api.period.settings(), []);
  const { data: closed } = useApi(() => api.period.closed(), []);
  const { data: month } = useApi(() => api.period.current(), []);
  const role = useRole();
  const canManage = role === 'accountant' || role === 'admin';
  const [reopenFor, setReopenFor] = useState<string | null>(null);
  const [reopenReason, setReopenReason] = useState('');
  const [stepId, setStepId] = useState<string | null>(null);
  const [panel, setPanel] = useState<Panel>('step');
  const [tab, setTab] = useState<Tab>('issues');
  const [confirming, setConfirming] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (!steps) return loadError ? <div className="pad"><Notice tone="warn">{loadError}</Notice></div> : null;
  if (month?.status === 'reopened') return canManage ? <ReclosePanel month={month} onDone={setMessage} /> : <div className="pad"><Notice tone="warn">{month.title}: месяц исправляет бухгалтер. Начисления сейчас недоступны.</Notice></div>;
  const current = steps.find((step) => step.id === stepId) ?? steps.find((step) => step.state === 'current') ?? steps[steps.length - 1]!;

  const hasPreview = current.id === 'charges' && current.state !== 'done';
  const activeTab: Tab = hasPreview ? tab : 'issues';
  const tabs: Array<{ value: Tab; label: string }> = [
    { value: 'issues', label: `Что проверить · ${current.issues.length}` },
    ...(hasPreview ? [{ value: 'preview' as const, label: 'Пробный расчёт' }] : []),
  ];

  const selectStep = (step: PeriodStep) => { setStepId(step.id); setTab('issues'); setPanel('step'); setConfirming(false); setMessage(null); setError(null); };

  const run = async () => {
    setError(null);
    try {
      const result = await api.period.run(current.id);
      setMessage(result.message);
    } catch (reason) {
      setError(errorText(reason));
    }
    setConfirming(false);
  };

  const guarded = async (action: () => Promise<{ message: string }>) => {
    setError(null);
    try {
      setMessage((await action()).message);
      setReopenFor(null);
      setReopenReason('');
      setPanel('step');
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  const onAction = () => {
    if (current.id === 'readings') { const next = steps[1]; if (next) selectStep(next); return; }
    if (current.confirmTitle) setConfirming(true);
    else void run();
  };

  return (
    <>
      <div className="stack" style={{ padding: '10px 16px', gap: 8, borderBottom: '1px solid var(--line)' }}>
        <div className="between" style={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <h1 className="title">{month?.title ?? ''}{month?.status === 'closed' ? ', закрыт' : ''}</h1>
          <div className="row">
            <Link to="/period/readiness" className="btn" style={{ width: 160 }}>Готовность месяца</Link>
            {canManage && month?.status === 'open' && <Link to="/period/cancel" className="btn" style={{ width: 160 }}>Отмена начислений</Link>}
            <Button style={{ width: 160 }} size="auto" active={panel === 'settings'} onClick={() => setPanel(panel === 'settings' ? 'step' : 'settings')}>Параметры</Button>
            <Button style={{ width: 160 }} size="auto" active={panel === 'closed'} onClick={() => setPanel(panel === 'closed' ? 'step' : 'closed')}>Закрытые месяцы</Button>
          </div>
        </div>
        <ol className="steps" aria-label="Шаги месяца">
          {steps.map((step, index) => (
            <li key={step.id}>
              <button type="button" className={`step${step.state === 'done' ? ' step-done' : ''}`} aria-current={step.id === current.id ? 'step' : undefined} onClick={() => selectStep(step)}>
                <span className="step-name">{index + 1}. {step.name}</span>
                <span className="step-sub">{step.status}</span>
              </button>
            </li>
          ))}
        </ol>
      </div>

      <div className="split">
        <main className="main">
          <Tabs label="Содержимое шага" items={tabs} value={activeTab} onChange={setTab} />
          {activeTab === 'preview' ? <ChargePreviewPanel month={month?.title ?? 'Месяц'} /> : <div className="scroll">
            {current.issues.map((issue) => (
              <div key={issue.title} style={{ display: 'flex', flexWrap: 'wrap', gap: '6px 16px', alignItems: 'center', justifyContent: 'space-between', padding: '12px 16px', borderBottom: '1px solid var(--line-soft)' }}>
                <div style={{ flex: '1 1 340px', minWidth: 0 }}>
                  <div className={`strong${issue.warn ? ' warn' : ''}`}>{issue.title}</div>
                  <div className="small muted" style={{ marginTop: 2, lineHeight: 1.4 }}>{issue.details}</div>
                </div>
                <Link to={issue.to} className="btn" style={{ width: 148, flex: '0 0 148px' }}>{issue.action}</Link>
              </div>
            ))}
          </div>}
        </main>

        <aside className="pane pane-right" style={{ width: 344 }}>
          {panel === 'step' && (
            <>
              <div className="scroll stack pad">
                <h2 style={{ fontSize: 17, fontWeight: 600 }}>{current.name}</h2>
                <p className="muted" style={{ fontSize: 13.5, lineHeight: 1.5 }}>{current.description}</p>
                <DefinitionList items={current.stats.map((stat) => ({ label: stat.label, value: stat.value, ...(stat.warn ? { warn: true } : {}) }))} />
                {message && <Notice tone="info">{message}</Notice>}
                {error && <Notice tone="warn">{error}</Notice>}
              </div>
              <div className="stack pad" style={{ background: 'var(--surface)', borderTop: '1px solid var(--line)', gap: 8 }}>
                {confirming ? (
                  <>
                    <Notice tone="warn"><strong style={{ display: 'block', marginBottom: 2 }}>{current.confirmTitle}</strong>{current.confirmText}</Notice>
                    <Button variant="danger" size="lg" onClick={() => void run()}>{current.confirmAction}</Button>
                    <Button size="full" onClick={() => setConfirming(false)}>Отмена</Button>
                  </>
                ) : month?.status === 'closed' && canManage ? (
                  <Button variant="primary" size="lg" onClick={() => void guarded(() => api.period.openNext())}>Открыть месяц: {month.nextTitle}</Button>
                ) : (
                  <Button variant="primary" size="lg" disabled={current.state === 'done' && current.id !== 'readings'} onClick={onAction}>{current.state === 'done' && current.id !== 'readings' ? 'Шаг выполнен' : current.action}</Button>
                )}
              </div>
            </>
          )}
          {panel === 'settings' && (
            <div className="scroll stack pad">
              <h2 style={{ fontSize: 17, fontWeight: 600 }}>Параметры расчёта</h2>
              <p className="small muted">Редактирование будет подключено вместе с формулами начисления.</p>
              {settings?.map((item) => (
                <div key={item.title} className="card" style={{ padding: '10px 12px' }}>
                  <span className="strong" style={{ display: 'block' }}>{item.title}</span>
                  <span className="small muted" style={{ display: 'block', marginTop: 2 }}>{item.details}</span>
                </div>
              ))}
            </div>
          )}
          {panel === 'closed' && (
            <div className="scroll stack pad">
              <h2 style={{ fontSize: 17, fontWeight: 600 }}>Закрытые месяцы</h2>
              {closed?.length === 0 && <p className="muted">Закрытых месяцев пока нет.</p>}
              {closed?.map((item) => (
                <div key={item.period} className="card stack" style={{ padding: '10px 12px', gap: 6 }}>
                  <div className="between strong"><span>{item.month}</span><span className="mono small">{item.closedAt}</span></div>
                  <div className="small muted">{item.closedBy ? `Закрыл: ${item.closedBy}.` : ''}{item.reclosedCount > 0 ? ` Перезакрытий: ${item.reclosedCount}, последнее ${item.reclosedAt}.` : ''}</div>
                  {canManage && (item.cannotReopen ? <div className="small muted">{item.cannotReopen}</div> : reopenFor === item.period ? (
                    <>
                      <Field label="Причина исправления">{(id) => <TextInput id={id} autoFocus value={reopenReason} onChange={(event) => setReopenReason(event.target.value)} />}</Field>
                      {error && <Notice tone="warn">{error}</Notice>}
                      <Button variant="danger" size="full" disabled={!reopenReason.trim()} onClick={() => void guarded(() => api.period.reopen(item.period, reopenReason.trim()))}>Открыть на исправление</Button>
                      <Button size="full" onClick={() => setReopenFor(null)}>Отмена</Button>
                    </>
                  ) : <Button size="full" onClick={() => { setError(null); setReopenFor(item.period); }}>Исправить месяц</Button>)}
                </div>
              ))}
            </div>
          )}
        </aside>
      </div>
    </>
  );
}
