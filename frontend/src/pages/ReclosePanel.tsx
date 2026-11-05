import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { formatMoney } from '@/domain/billing';
import { formatPrice } from '@/domain/tariffs';
import type { ChargeIssue, CurrentMonth, RecloseChange } from '@/domain/types';
import { Button, DataTable, Field, Notice, TextInput, type Column } from '@/ui';

const signed = (value: number) => `${value > 0 ? '+' : ''}${formatMoney(value)}`;
const price = (value: number | null) => (value === null ? '—' : formatPrice(value));

const CHANGE_COLUMNS: Column<RecloseChange>[] = [
  { key: 'abonent', header: 'Абонент', render: (row) => <span className="strong">{row.abonent}</span> },
  { key: 'service', header: 'Услуга', render: (row) => row.service },
  { key: 'oldPrice', header: 'Цена была, ₽', align: 'right', mono: true, render: (row) => price(row.oldPrice) },
  { key: 'newPrice', header: 'Цена стала, ₽', align: 'right', mono: true, render: (row) => price(row.newPrice) },
  { key: 'oldAmount', header: 'Было, ₽', align: 'right', mono: true, render: (row) => formatMoney(row.oldAmount) },
  { key: 'newAmount', header: 'Стало, ₽', align: 'right', mono: true, render: (row) => formatMoney(row.newAmount) },
  { key: 'delta', header: 'Разница, ₽', align: 'right', mono: true, render: (row) => <span className={row.delta !== 0 ? 'strong' : 'muted'}>{signed(row.delta)}</span> },
];

const ISSUE_COLUMNS: Column<ChargeIssue>[] = [
  { key: 'abonent', header: 'Абонент', render: (row) => row.abonent },
  { key: 'service', header: 'Услуга', render: (row) => row.service },
  { key: 'problem', header: 'Ошибка', render: (row) => <span className="warn strong">{row.problem}</span> },
];

/** Месяц открыт на исправление: что изменится и подтверждение. */
export function ReclosePanel({ month, onDone }: { month: CurrentMonth; onDone: (message: string) => void }) {
  const { data: preview, error: loadError } = useApi(() => api.period.reclosePreview(), []);
  const [reason, setReason] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const apply = async () => {
    if (!preview) return;
    setError(null);
    try {
      onDone((await api.period.reclose(reason.trim(), preview.delta)).message);
    } catch (reason_) {
      setError(errorText(reason_));
    }
    setConfirming(false);
  };

  const blocked = (preview?.blocking.length ?? 0) > 0;

  return (
    <>
      <div className="bar" style={{ justifyContent: 'space-between' }}>
        <div>
          <h1 className="title">{month.title}: исправление</h1>
          <p className="small muted" style={{ marginTop: 2 }}>Причина открытия: {month.reopenReason}</p>
        </div>
      </div>
      <div className="split">
        <main className="main">
          {loadError && <div className="pad"><Notice tone="warn">{loadError}</Notice></div>}
          {preview && (
            <div className="scroll">
              {blocked && <div className="pad"><Notice tone="warn">Есть ошибки: по этим услугам уже было начисление, но посчитать его заново нельзя. Исправьте их, чтобы завершить.</Notice></div>}
              {preview.blocking.length > 0 && <DataTable columns={ISSUE_COLUMNS} rows={preview.blocking} rowKey={(row) => `${row.abonent}-${row.service}`} />}
              <DataTable columns={CHANGE_COLUMNS} rows={preview.changes} rowKey={(row) => `${row.abonentId}-${row.serviceId}`} minWidth={760} empty={<span className="muted">Расчёт совпадает с закрытым месяцем. Изменений нет.</span>} />
            </div>
          )}
        </main>
        <aside className="pane pane-right" style={{ width: 344 }} aria-label="Перезакрытие">
          <div className="scroll stack pad">
            <h2 style={{ fontSize: 17, fontWeight: 600 }}>Перезакрытие</h2>
            <p className="muted" style={{ fontSize: 13.5, lineHeight: 1.5 }}>Измените цены, тарифы услуг или показания за этот месяц. Деньги изменятся только после подтверждения, показания влияют на долг сразу.</p>
            <div className="row" style={{ flexWrap: 'wrap' }}>
              <Link to="/directories?name=Тарифы" className="btn" style={{ width: 148 }}>Цены</Link>
              <Link to="/readings" className="btn" style={{ width: 148 }}>Показания</Link>
            </div>
            <Notice tone="info">Пени не пересчитываются. Разница попадёт в текущий долг абонента или в переплату.</Notice>
            {preview && (
              <div className="between strong"><span>Изменится строк: {preview.changes.length}</span><span className="mono">{signed(preview.delta)} ₽</span></div>
            )}
            <Field label="Основание">{(id) => <TextInput id={id} placeholder="Например, приказ № 12" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
            {error && <Notice tone="warn">{error}</Notice>}
          </div>
          <div className="stack pad" style={{ background: 'var(--surface)', borderTop: '1px solid var(--line)', gap: 8 }}>
            {confirming && preview ? (
              <>
                <Notice tone="warn"><strong style={{ display: 'block', marginBottom: 2 }}>Провести разницу {signed(preview.delta)} ₽?</strong>Начисления месяца будут заменены, старые значения останутся в истории.</Notice>
                <Button variant="danger" size="lg" onClick={() => void apply()}>Да, перезакрыть</Button>
                <Button size="full" onClick={() => setConfirming(false)}>Отмена</Button>
              </>
            ) : (
              <Button variant="primary" size="lg" disabled={!preview || blocked || !reason.trim()} onClick={() => setConfirming(true)}>Перезакрыть месяц</Button>
            )}
          </div>
        </aside>
      </div>
    </>
  );
}
