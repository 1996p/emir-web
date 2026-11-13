import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { AbonentSelect, abonentAddress } from './AbonentSelect';
import { api, errorText, useApi } from '@/api';
import { balanceOf, formatBalance, formatMoney, parseMoney } from '@/domain/billing';
import type { CorrectionType } from '@/domain/types';
import { useRole } from '@/layout/role';
import { Button, Field, Notice, SelectInput, TextArea, TextInput } from '@/ui';

const GROUPS: CorrectionType['group'][] = ['Платежи', 'Начисления', 'Баланс'];

export function CorrectionsPage() {
  const { data: month } = useApi(() => api.period.current(), []);
  const [params] = useSearchParams();
  const { data: types } = useApi(() => api.corrections.types(), []);
  const { data: abonents } = useApi(() => api.abonents.list(), []);
  const [typeId, setTypeId] = useState('cancel-payment');
  const [amountText, setAmountText] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [chosenId, setChosenId] = useState<string | null>(null);
  const today = new Date().toLocaleDateString('sv-SE');
  const [operationDate, setOperationDate] = useState(today);
  const [paymentId, setPaymentId] = useState<string | null>(null);
  // Перенос: верный абонент для «между адресами», услуги «откуда» и «куда» для «между услугами» (credit — переплата)
  const [targetId, setTargetId] = useState<string | null>(null);
  const [fromService, setFromService] = useState('');
  const [toService, setToService] = useState('');
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const role = useRole();
  // Пришли из карточки абонента: абонент закреплён. Открыли раздел сами: выбирают из списка, заранее никто не подставляется
  const fromCard = params.get('id');
  const abonent = abonents?.find((item) => item.id === (fromCard ?? chosenId)) ?? null;
  const abonentId = abonent?.id ?? null;
  const { data: paymentList } = useApi(() => (abonentId ? api.abonents.payments(abonentId) : Promise.resolve([])), [abonentId]);
  const type = types?.find((item) => item.id === typeId) ?? null;
  const visibleGroups = role === 'cashier' ? GROUPS.filter((group) => group === 'Платежи') : GROUPS;

  if (!types || !type || !abonents) return null;

  const payments = (paymentList ?? []).filter((payment) => payment.status === 'Проведена');
  const payment = payments.find((item) => item.id === paymentId) ?? payments[0];
  const isCancel = type.id === 'cancel-payment';
  const amount = isCancel ? payment?.amount ?? 0 : parseMoney(amountText ?? formatMoney(type.defaultAmount)) ?? 0;
  const balance = abonent ? balanceOf(abonent) : 0;
  const after = balance + type.balanceEffect * amount;
  const isMoveAddress = type.id === 'move-address';
  const isMoveService = type.id === 'move-service';
  const target = isMoveAddress ? abonents.find((item) => item.id === targetId && item.id !== abonentId) ?? null : null;
  const serviceOptions = [...(abonent?.services ?? []).map((line) => ({ id: line.id, name: `${line.name}, долг ${formatMoney(line.debt)} ₽` })), { id: 'credit', name: `Переплата, ${formatMoney(abonent?.credit ?? 0)} ₽` }];
  const moveFrom = fromService || serviceOptions[0]?.id || '';
  const moveTo = toService || serviceOptions.find((item) => item.id !== moveFrom)?.id || '';
  const amountTooBig = isMoveAddress && payment !== undefined && amount > payment.amount;
  const cannot = !abonent || !operationDate || operationDate > today || amount <= 0 || !reason.trim() || (type.needsPayment && !payment)
    || (isMoveAddress && (!target || amountTooBig)) || (isMoveService && (!moveFrom || !moveTo || moveFrom === moveTo));

  const selectType = (next: CorrectionType) => { setTypeId(next.id); setAmountText(null); setDone(false); };
  const selectAbonent = (id: string) => { setChosenId(id); setPaymentId(null); setTargetId(null); setFromService(''); setToService(''); setDone(false); setError(null); };

  const submit = async () => {
    if (!abonent) return;
    setError(null);
    setDone(false);
    try {
      const move = isMoveAddress ? { targetAbonentId: target?.id } : isMoveService ? { fromServiceId: moveFrom, toServiceId: moveTo } : undefined;
      await api.corrections.apply(abonent.id, type.id, amount, reason, payment?.id, operationDate, move);
      setReason('');
      setAmountText(null);
      setPaymentId(null);
      setDone(true);
    } catch (failure) {
      setError(errorText(failure));
    }
  };

  return (
    <div className="split">
      <aside className="pane" style={{ width: 256 }}>
        <h1 style={{ padding: '14px 16px 8px', fontSize: 18, fontWeight: 700 }}>Корректировки</h1>
        <div className="scroll" role="group" aria-label="Выбор операции" style={{ padding: '0 8px 12px' }}>
          {visibleGroups.map((group) => (
            <div key={group} style={{ paddingTop: 8 }}>
              <div className="muted strong" style={{ fontSize: 12, padding: '0 8px 3px' }}>{group}</div>
              {types.filter((item) => item.group === group).map((item) => (
                <button key={item.id} type="button" className="nav-item" style={{ minHeight: 38 }} aria-pressed={item.id === typeId} onClick={() => selectType(item)}>{item.name}</button>
              ))}
            </div>
          ))}
        </div>
      </aside>

      <main className="main">
        <div className="scroll stack" style={{ padding: '16px 24px', gap: 14 }}>
          <div>
            <h2 style={{ fontSize: 22, fontWeight: 700 }}>{type.name}</h2>
            <p className="muted" style={{ marginTop: 4, lineHeight: 1.45, maxWidth: 640 }}>{type.description}</p>
          </div>
          {fromCard
            ? (
              <div>
                <Field label="Абонент" hint={abonent ? <>{abonentAddress(abonent)}. Абонент выбран в карточке. <Link to="/corrections">Выбрать другого</Link></> : undefined}>
                  {(id) => <TextInput id={id} readOnly aria-readonly="true" value={abonent ? `${abonent.name}, ЛС ${abonent.account}` : ''} style={{ background: 'var(--tint)', cursor: 'not-allowed' }} />}
                </Field>
                {!abonent && <Notice tone="warn">Абонент из ссылки не найден. <Link to="/corrections">Выбрать из списка</Link></Notice>}
              </div>
            )
            : (
              <div>
                <AbonentSelect label="Абонент" abonents={abonents} value={abonent} onChange={(item) => selectAbonent(item.id)} />
                {abonent && <div className="field-hint muted">{abonentAddress(abonent)}</div>}
              </div>
            )}
          <div className="grid-fields">
            <Field label={type.amountLabel}>{(id) => (
              <TextInput id={id} mono inputMode="decimal" readOnly={isCancel} value={isCancel ? formatMoney(amount) : amountText ?? formatMoney(type.defaultAmount)} onChange={(event) => setAmountText(event.target.value)} />
            )}</Field>
            <Field label="Дата операции" hint="Дата документа-основания. Деньги проводятся в открытом месяце">{(id) => <TextInput id={id} type="date" max={today} value={operationDate} onChange={(event) => setOperationDate(event.target.value)} />}</Field>
          </div>
          {type.needsPayment && (
            <fieldset style={{ margin: 0, padding: 0, border: 0 }}>
              <legend className="field-label" style={{ padding: 0 }}>Платёж</legend>
              {payments.map((item) => (
                <label key={item.id} style={{ display: 'flex', alignItems: 'center', gap: 10, minHeight: 44, padding: '0 12px', border: '1px solid var(--line)', borderRadius: 6, marginBottom: 4, cursor: 'pointer' }}>
                  <input type="radio" name="payment" checked={payment?.id === item.id} onChange={() => setPaymentId(item.id)} style={{ width: 18, height: 18, margin: 0 }} />
                  <span style={{ flex: '1 1 auto' }}>{item.date} · {item.source}</span>
                  <span className="mono strong">{formatMoney(item.amount)} ₽</span>
                </label>
              ))}
              {payments.length === 0 && <p className="muted">{abonent ? 'Подходящих платежей нет.' : 'Сначала выберите абонента.'}</p>}
            </fieldset>
          )}
          {isMoveAddress && (
            <div>
              <AbonentSelect label="Верный абонент" abonents={abonents.filter((item) => item.id !== abonentId)} value={target} onChange={(item) => setTargetId(item.id)} />
              {target && <div className="field-hint muted">{abonentAddress(target)}. Деньги и платёж перейдут к нему, кассовые отчёты не изменятся.</div>}
              {amountTooBig && <Notice tone="warn">Сумма больше платежа.</Notice>}
            </div>
          )}
          {isMoveService && abonent && (
            <div className="grid-fields">
              <Field label="Откуда">{(id) => (
                <SelectInput id={id} value={moveFrom} onChange={(event) => setFromService(event.target.value)}>
                  {serviceOptions.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
                </SelectInput>
              )}</Field>
              <Field label="Куда" hint={moveFrom === moveTo ? 'Выберите другую услугу' : 'Долг гасится, излишек уходит в переплату'}>{(id) => (
                <SelectInput id={id} value={moveTo} onChange={(event) => setToService(event.target.value)}>
                  {serviceOptions.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
                </SelectInput>
              )}</Field>
            </div>
          )}
          <Field label="Причина, обязательно">{(id) => <TextArea id={id} rows={2} value={reason} onChange={(event) => setReason(event.target.value)} placeholder="Например: ошибка в сумме при приёме в кассе" />}</Field>
        </div>
      </main>

      <aside className="pane pane-right" style={{ width: 360 }}>
        <div className="scroll stack pad" style={{ gap: 12 }}>
          <h3 style={{ fontSize: 16, fontWeight: 600 }}>Что изменится</h3>
          <div className="stack" style={{ gap: 4 }} aria-label="Движение денег">
            {type.flow.map((step, index) => (
              <div key={step}>
                <div className="card" style={{ padding: '9px 12px', fontWeight: 500 }}>{step}</div>
                {index < type.flow.length - 1 && <div className="muted" aria-hidden="true" style={{ textAlign: 'center', lineHeight: 1.2 }}>↓</div>}
              </div>
            ))}
          </div>
          <div className="card stack" style={{ padding: 12, gap: 8 }}>
            <div className="between small"><span className="muted">Баланс сейчас</span><span className="mono strong">{formatBalance(balance)} ₽</span></div>
            <div className="between" style={{ fontSize: 15 }}><strong>Баланс после</strong><span className="mono strong">{formatBalance(after)} ₽</span></div>
            <div className="between small" style={{ borderTop: '1px solid var(--line-soft)', paddingTop: 8 }}><span className="muted">Период</span><span>{month?.title ?? ''}{month?.status === 'closed' ? ', закрыт' : ', открыт'}</span></div>
          </div>
          <p className="small muted" style={{ lineHeight: 1.45 }}>Исходная запись останется. Операция и причина появятся в журнале абонента.</p>
          {done && abonent && <Notice tone="ok">Проведено. <Link to={`/abonents?id=${abonent.id}`}>Открыть абонента</Link></Notice>}
          {error && <Notice tone="warn">{error}</Notice>}
        </div>
        <div className="pad" style={{ background: 'var(--surface)', borderTop: '1px solid var(--line)' }}>
          <Button variant="primary" size="lg" disabled={cannot} onClick={() => void submit()}>
            {!abonent ? 'Выберите абонента' : isMoveAddress && !target ? 'Выберите верного абонента' : !reason.trim() ? 'Укажите причину' : amount <= 0 ? 'Укажите сумму' : `${type.actionVerb} ${formatMoney(amount)} ₽`}
          </Button>
        </div>
      </aside>
    </div>
  );
}
