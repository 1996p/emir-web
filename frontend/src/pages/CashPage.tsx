import { useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { balanceOf, chargeForReading, formatBalance, formatMoney, parseMoney, parseReading } from '@/domain/billing';
import type { Abonent, PaymentAllocation, PaymentSource, ServiceId } from '@/domain/types';
import { Button, Field, LinkButton, Segmented, TextArea, TextInput } from '@/ui';

type Mode = 'pay' | 'refund';

const SOURCES: Array<{ value: PaymentSource; label: string }> = [
  { value: 'cash-person', label: 'Касса ФЛ' }, { value: 'bank', label: 'Банк' }, { value: 'cash-org', label: 'Касса ЮЛ' },
];
const MODES: Array<{ value: Mode; label: string }> = [{ value: 'pay', label: 'Оплата' }, { value: 'refund', label: 'Возврат' }];

interface DoneInfo {
  title: string;
  text: string;
}

export function CashPage() {
  const [params, setParams] = useSearchParams();
  const { data: abonents } = useApi(() => api.abonents.list(), []);
  const { data: shift } = useApi(() => api.cash.shift(), []);
  const [mode, setMode] = useState<Mode>('pay');
  const [source, setSource] = useState<PaymentSource>('cash-person');
  const [query, setQuery] = useState('');
  const [done, setDone] = useState<DoneInfo | null>(null);

  const selectedId = params.get('id');
  const abonent = abonents?.find((item) => item.id === selectedId) ?? null;

  const matches = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    if (!normalized) return [];
    return (abonents ?? []).filter((item) => `${item.name} ${item.street} ${item.flat ?? ''} ${item.account}`.toLowerCase().includes(normalized));
  }, [abonents, query]);

  const pick = (id: string) => { setParams({ id }, { replace: true }); setQuery(''); setDone(null); };
  const next = () => { setParams({}, { replace: true }); setQuery(''); setDone(null); setMode('pay'); };
  const onSearchKey = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter' && matches[0]) pick(matches[0].id);
  };

  return (
    <>
      <div className="bar bar-tint">
        <Segmented label="Операция" options={MODES} value={mode} onChange={(value) => { setMode(value); setDone(null); }} />
        <div style={{ flex: '1 1 340px', minWidth: 0, position: 'relative' }}>
          <TextInput primary autoFocus type="search" aria-label="Найти абонента" value={query} onKeyDown={onSearchKey} onChange={(event) => setQuery(event.target.value)} placeholder="Найти абонента: ФИО, лицевой счёт или адрес, затем Enter" />
          {query.trim() && (
            <div style={{ position: 'absolute', top: 46, left: 0, right: 0, zIndex: 5, background: 'var(--surface)', border: '1px solid var(--field)', borderRadius: 6, maxHeight: 320, overflowY: 'auto' }}>
              {matches.map((item) => <AbonentRow key={item.id} abonent={item} onPick={() => pick(item.id)} />)}
              {matches.length === 0 && <div className="pad">Никого не нашли.</div>}
            </div>
          )}
        </div>
        <Segmented label="Откуда платёж" options={SOURCES} value={source} onChange={setSource} itemWidth={88} />
      </div>

      <div className="split">
        {abonent ? (
          mode === 'pay'
            ? <PaymentForm key={`${abonent.id}-pay`} abonent={abonent} source={source} done={done} onDone={setDone} onNext={next} shift={shift} />
            : <RefundForm key={`${abonent.id}-refund`} abonent={abonent} done={done} onDone={setDone} onNext={next} shift={shift} />
        ) : (
          <>
            <main className="main">
              <div className="scroll" style={{ padding: '24px 16px', maxWidth: 620 }}>
                <h1 className="title">Следующий абонент</h1>
                <p className="muted" style={{ margin: '6px 0 14px' }}>Введите данные в поле поиска или выберите из недавних.</p>
                {(abonents ?? []).slice(2, 6).map((item) => <AbonentRow key={item.id} abonent={item} onPick={() => pick(item.id)} />)}
              </div>
            </main>
            <aside className="pane pane-right" style={{ width: 360 }}><ShiftList shift={shift} /></aside>
          </>
        )}
      </div>
    </>
  );
}

function AbonentRow({ abonent, onPick }: { abonent: Abonent; onPick: () => void }) {
  const balance = balanceOf(abonent);
  return (
    <button type="button" onClick={onPick} style={{ display: 'flex', justifyContent: 'space-between', gap: 16, width: '100%', minHeight: 48, padding: '6px 12px', textAlign: 'left', background: 'transparent', border: 0, borderBottom: '1px solid var(--line-soft)' }}>
      <span>
        <span className="strong" style={{ display: 'block' }}>{abonent.name}</span>
        <span className="muted" style={{ display: 'block', fontSize: 12.5 }}>{abonent.street}{abonent.flat ? `-${abonent.flat}` : ''} · ЛС {abonent.account}</span>
      </span>
      <span className={`mono nowrap${balance < 0 ? ' warn' : ''}`}>{formatBalance(balance)}</span>
    </button>
  );
}

function AbonentHeader({ abonent }: { abonent: Abonent }) {
  const balance = balanceOf(abonent);
  return (
    <header className="bar" style={{ justifyContent: 'space-between', gap: '6px 24px' }}>
      <div>
        <h1 className="title"><Link to={`/abonents?id=${abonent.id}`} style={{ color: 'var(--ink)', textDecoration: 'none' }}>{abonent.name}</Link></h1>
        <p className="muted" style={{ marginTop: 2, fontSize: 13.5 }}>{abonent.street}{abonent.flat ? `-${abonent.flat}` : ''} · ЛС {abonent.account}</p>
      </div>
      <div className={`right${balance < 0 ? ' warn' : ''}`}>
        <div className="mono" style={{ fontSize: 20, fontWeight: 500 }}>{formatBalance(balance)} ₽</div>
        <div style={{ fontSize: 12.5 }}>{balance < 0 ? 'долг' : balance > 0 ? 'переплата' : 'долга нет'}</div>
      </div>
    </header>
  );
}

type ShiftData = Awaited<ReturnType<typeof api.cash.shift>> | null;

function ShiftList({ shift }: { shift: ShiftData }) {
  return (
    <>
      <div className="between" style={{ padding: '10px 16px 6px' }}>
        <h2 style={{ fontSize: 14, fontWeight: 600 }}>Смена сегодня · {shift?.count ?? 0}</h2>
        <Link to="/reports" className="small" style={{ fontWeight: 500 }}>Итоги: {formatMoney(shift?.total ?? 0)} ₽</Link>
      </div>
      <div className="scroll" style={{ padding: '0 16px 12px' }}>
        {shift?.entries.map((entry) => (
          <div key={entry.id} className="between small" style={{ padding: '7px 0', borderTop: '1px solid #dce4ec' }}>
            <span><span className="mono muted">{entry.time}</span> {entry.name}</span>
            <span className={`mono nowrap strong${entry.amount < 0 ? ' warn' : ''}`}>{entry.amount < 0 ? '−' : ''}{formatMoney(entry.amount)}</span>
          </div>
        ))}
      </div>
    </>
  );
}

function DoneCard({ done, onNext, abonentId }: { done: DoneInfo; onNext: () => void; abonentId: string }) {
  return (
    <div className="stack" role="status">
      <div className="ok" style={{ fontSize: 17, fontWeight: 700 }}>{done.title}</div>
      <div style={{ lineHeight: 1.5 }}>{done.text}</div>
      <Button size="full" style={{ height: 44 }}>Напечатать квитанцию</Button>
      <LinkButton to={`/corrections?id=${abonentId}`} size="full">Отменить операцию</LinkButton>
      <Button variant="primary" size="lg" autoFocus onClick={onNext}>Следующий абонент</Button>
    </div>
  );
}

interface FormProps {
  abonent: Abonent;
  done: DoneInfo | null;
  onDone: (info: DoneInfo) => void;
  onNext: () => void;
  shift: ShiftData;
}

function PaymentForm({ abonent, source, done, onDone, onNext, shift }: FormProps & { source: PaymentSource }) {
  const [unchecked, setUnchecked] = useState<Partial<Record<ServiceId, boolean>>>({});
  const [edited, setEdited] = useState<Partial<Record<ServiceId, string>>>({});
  // Показание у каждого счётчика своё: у абонента может быть несколько счётчиков (холодная и горячая вода)
  const [readings, setReadings] = useState<Partial<Record<ServiceId, string>>>({});
  const [cash, setCash] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());
  const [busy, setBusy] = useState(false);
  const bank = source === 'bank';

  const rows = abonent.services.map((line) => {
    const reading = readings[line.id] ?? '';
    const readingValue = line.meter ? parseReading(reading) : null;
    const charge = chargeForReading(line, readingValue);
    const auto = line.debt + charge;
    const text = edited[line.id];
    const amount = text !== undefined ? parseMoney(text) ?? 0 : auto;
    const checked = !unchecked[line.id];
    const low = Boolean(line.meter && readingValue !== null && readingValue < line.meter.lastReading);
    const usage = line.meter && readingValue !== null && !low ? readingValue - line.meter.lastReading : 0;
    return { line, charge, auto, text, amount, checked, low, usage, reading, readingValue };
  });
  const total = rows.reduce((sum, row) => sum + (row.checked ? Math.max(row.amount, 0) : 0), 0);
  const cashValue = parseMoney(cash) ?? 0;
  const difference = cashValue - total;
  const short = !bank && (cash === '' || difference < 0);
  const hasLowReading = rows.some((row) => row.low);
  const cannot = total <= 0 || short || hasLowReading || busy;

  useEffect(() => { setError(null); }, [cash, total]);

  const submit = async () => {
    if (cannot) return;
    const allocations: PaymentAllocation[] = rows.filter((row) => row.checked && row.amount > 0).map((row) => ({ serviceId: row.line.id, amount: row.amount }));
    setBusy(true);
    try {
      const entered = rows.filter((row) => row.readingValue !== null).map((row) => ({ serviceId: row.line.id, value: row.readingValue as number }));
      const request = { abonentId: abonent.id, source, allocations, ...(entered.length > 0 ? { readings: entered } : {}) };
      const result = await api.cash.pay(request, idempotencyKey);
      setIdempotencyKey(crypto.randomUUID());
      setEdited({}); setReadings({}); setCash(''); setUnchecked({});
      onDone({
        title: 'Оплата проведена',
        text: `${formatMoney(result.total)} ₽ от ${abonent.shortName}.${bank ? '' : ` Сдача ${formatMoney(difference)} ₽.`}${entered.length > 0 ? (entered.length > 1 ? ' Показания сохранены.' : ' Показание сохранено.') : ''} Баланс: ${formatBalance(result.newBalance)} ₽.`,
      });
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  };

  const label = hasLowReading ? 'Проверьте показание' : total <= 0 ? 'Выберите услугу' : short ? 'Введите принятую сумму' : `Провести ${formatMoney(total)} ₽`;

  return (
    <>
      <main className="main">
        <AbonentHeader abonent={abonent} />
        <div className="scroll">
          <table className="table" style={{ minWidth: 640 }}>
            <thead>
              <tr><th>Оплатить</th><th>Услуга</th><th className="right">Долг, ₽</th><th>Было</th><th>Новое показание</th><th>К оплате, ₽</th></tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.line.id}>
                  <td><input type="checkbox" className="check" checked={row.checked} aria-label={`Оплатить услугу ${row.line.name}`} onChange={() => setUnchecked((current) => ({ ...current, [row.line.id]: row.checked }))} /></td>
                  <td>
                    <div className="strong">{row.line.name}</div>
                    <div className="muted" style={{ fontSize: 12.5, marginTop: 2 }}>{row.line.meter ? `Счётчик ${row.line.meter.number}, ${formatMoney(row.line.meter.tariff)} ₽ за единицу` : row.line.tariffName}</div>
                  </td>
                  <td className="right mono">{formatMoney(row.line.debt)}</td>
                  <td className="mono">{row.line.meter ? row.line.meter.lastReading : '—'}</td>
                  <td>
                    {row.line.meter && (
                      <>
                        <TextInput mono inputMode="decimal" aria-label={`Новое показание: ${row.line.name}`} value={row.reading} style={{ width: 100, minHeight: 38 }} onChange={(event) => { const value = event.target.value; setReadings((current) => ({ ...current, [row.line.id]: value })); setEdited(({ [row.line.id]: _dropped, ...rest }) => rest); }} />
                        {row.low && <div className="field-hint warn">Меньше предыдущего</div>}
                        {row.usage > 0 && <div className="field-hint ok">Расход: {Math.round(row.usage * 10000) / 10000}{row.line.meter.unit ? ` ${row.line.meter.unit}` : ''}</div>}
                      </>
                    )}
                  </td>
                  <td>
                    <TextInput mono alignRight edited={row.text !== undefined} inputMode="decimal" aria-label={`Сумма за услугу ${row.line.name}`} style={{ width: 120, minHeight: 38 }} value={row.text ?? formatMoney(row.auto)} onChange={(event) => setEdited((current) => ({ ...current, [row.line.id]: event.target.value }))} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </main>
      <aside className="pane pane-right" style={{ width: 360 }}>
        <div className="stack pad" style={{ background: 'var(--surface)', borderBottom: '1px solid var(--line)' }}>
          {done ? <DoneCard done={done} onNext={onNext} abonentId={abonent.id} /> : (
            <>
              <div className="between">
                <span className="muted">Итого</span>
                <span className="mono" style={{ fontSize: 26, fontWeight: 500 }}>{formatMoney(total)} ₽</span>
              </div>
              {bank ? (
                <p className="small muted" style={{ lineHeight: 1.45 }}>Безналичный платёж: сдачи нет, в реестре он будет отмечен офисом «Банковские платежи».</p>
              ) : (
                <>
                  <Field label="Принято наличными">{(id) => <TextInput id={id} mono inputMode="decimal" value={cash} onChange={(event) => setCash(event.target.value)} style={{ height: 46, fontSize: 18 }} />}</Field>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 4 }}>
                    <Button size="full" style={{ height: 36 }} onClick={() => setCash(formatMoney(total))}>Ровно</Button>
                    {[1000, 2000, 5000].map((value) => <Button key={value} size="full" style={{ height: 36 }} onClick={() => setCash(String(value))}>{value.toLocaleString('ru-RU')}</Button>)}
                  </div>
                  <div className={`between${cash !== '' && difference < 0 ? ' warn' : ''}`}>
                    <span>{cash !== '' && difference < 0 ? 'Не хватает' : 'Сдача'}</span>
                    <span className="mono" style={{ fontSize: 20, fontWeight: 500 }}>{formatMoney(cash === '' ? 0 : difference)} ₽</span>
                  </div>
                </>
              )}
              {error && <div className="small warn">{error}</div>}
              <Button variant="primary" size="lg" disabled={cannot} onClick={() => void submit()}>{label}</Button>
            </>
          )}
        </div>
        <ShiftList shift={shift} />
      </aside>
    </>
  );
}

function RefundForm({ abonent, done, onDone, onNext, shift }: FormProps) {
  const { data: payments } = useApi(() => api.abonents.payments(abonent.id), [abonent.id]);
  const [paymentId, setPaymentId] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const available = (payments ?? []).filter((payment) => payment.status === 'Проведена');
  const selected = available.find((payment) => payment.id === paymentId) ?? available[0];
  const amount = selected?.amount ?? 0;
  const [error, setError] = useState<string | null>(null);
  const cannot = !selected || !reason.trim();

  const submit = async () => {
    if (!selected) return;
    setError(null);
    try {
      const refunded = await api.cash.refund(abonent.id, selected.id, reason);
      onDone({ title: 'Возврат выполнен', text: `Выдайте ${formatMoney(refunded.amount)} ₽. Причина записана в журнал абонента.` });
      setReason('');
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <>
      <main className="main">
        <AbonentHeader abonent={abonent} />
        <div className="scroll stack pad">
          <h2 className="section-title">Какой платёж вернуть</h2>
          {available.map((payment) => (
            <button key={payment.id} type="button" aria-pressed={selected?.id === payment.id} onClick={() => setPaymentId(payment.id)} style={{ display: 'flex', justifyContent: 'space-between', gap: 16, alignItems: 'center', minHeight: 52, padding: '0 14px', textAlign: 'left', borderRadius: 6, border: `2px solid ${selected?.id === payment.id ? 'var(--brand)' : 'var(--line)'}`, background: selected?.id === payment.id ? 'var(--info-bg)' : 'var(--surface)' }}>
              <span>{payment.date} · {payment.source}</span>
              <span className="mono strong">{formatMoney(payment.amount)} ₽</span>
            </button>
          ))}
          {available.length === 0 && <p className="muted">У абонента нет платежей для возврата.</p>}
          <Field label="Причина возврата, обязательно">{(id) => <TextArea id={id} rows={2} value={reason} onChange={(event) => setReason(event.target.value)} placeholder="Например: заплатили дважды" />}</Field>
        </div>
      </main>
      <aside className="pane pane-right" style={{ width: 360 }}>
        <div className="stack pad" style={{ background: 'var(--surface)', borderBottom: '1px solid var(--line)' }}>
          {done ? <DoneCard done={done} onNext={onNext} abonentId={abonent.id} /> : (
            <>
              <div className="between">
                <span className="muted">Вернуть</span>
                <span className="mono" style={{ fontSize: 26, fontWeight: 500 }}>{formatMoney(amount)} ₽</span>
              </div>
              {error && <div className="small warn">{error}</div>}
              <Button variant="primary" size="lg" disabled={cannot} onClick={() => void submit()}>{!selected ? 'Нет платежей' : !reason.trim() ? 'Укажите причину' : `Вернуть ${formatMoney(amount)} ₽`}</Button>
            </>
          )}
        </div>
        <ShiftList shift={shift} />
      </aside>
    </>
  );
}
