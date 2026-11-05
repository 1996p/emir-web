import { useState } from 'react';
import { api, errorText, useApi } from '@/api';
import { formatPeriod, formatPrice, nextPeriod, parsePrice } from '@/domain/tariffs';
import type { TariffItem } from '@/domain/types';
import { useRole } from '@/layout/role';
import { Button, DataTable, Field, Notice, TextInput, type Column } from '@/ui';

const SCHEMES: Record<string, string> = { HeatHouseNorm: 'По площади и нормативу', MeterApartment: 'По счётчику' };

export function TariffsDirectory({ filter }: { filter: string }) {
  const role = useRole();
  const { data, error } = useApi(() => api.tariffs.list(), []);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const open = data?.openPeriod ?? '';
  const needle = filter.trim().toLowerCase();
  const rows = (data?.tariffs ?? []).filter((tariff) => !needle || tariff.name.toLowerCase().includes(needle));
  const selected = data?.tariffs.find((tariff) => tariff.id === selectedId) ?? rows[0];
  const upcoming = (tariff: TariffItem) => tariff.prices.filter((price) => price.fromPeriod > open).slice(-1)[0];

  const columns: Column<TariffItem>[] = [
    { key: 'name', header: 'Тариф', render: (tariff) => tariff.name },
    { key: 'scheme', header: 'Схема расчёта', render: (tariff) => SCHEMES[tariff.scheme] ?? tariff.scheme },
    { key: 'current', header: 'Цена сейчас, ₽', render: (tariff) => tariff.currentPrice === null ? 'нет цены' : formatPrice(tariff.currentPrice) },
    { key: 'next', header: 'Следующая цена', render: (tariff) => { const next = upcoming(tariff); return next ? `${formatPrice(next.price)}, ${formatPeriod(next.fromPeriod)}` : '—'; } },
    { key: 'lines', header: 'Услуг', render: (tariff) => tariff.lines },
  ];

  return (
    <div className="split">
      <div className="main">
        {error && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{error}</Notice></div>}
        <DataTable columns={columns} rows={rows} rowKey={(tariff) => tariff.id} onRowClick={(tariff) => setSelectedId(tariff.id)} isSelected={(tariff) => tariff.id === selected?.id} empty={<span className="muted">Тарифов нет.</span>} />
      </div>
      {selected && <TariffPanel key={`${selected.id}-${open}-${data?.reopened}`} tariff={selected} openPeriod={open} reopened={data?.reopened ?? false} canEdit={role === 'accountant' || role === 'admin'} />}
    </div>
  );
}

function TariffPanel({ tariff, openPeriod, reopened, canEdit }: { tariff: TariffItem; openPeriod: string; reopened: boolean; canEdit: boolean }) {
  const [fromPeriod, setFromPeriod] = useState(reopened ? openPeriod : nextPeriod(openPeriod));
  const [price, setPrice] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const parsed = parsePrice(price);

  const run = async (action: () => Promise<void>, done: string) => {
    setError(null);
    setNote(null);
    try {
      await action();
      setNote(done);
      setPrice('');
      setReason('');
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <aside className="pane pane-right" aria-label="История цен" style={{ width: 360 }}>
      <div className="scroll stack pad">
        <h2 className="section-title">История цен</h2>
        <p className="small muted">{tariff.name}</p>
        {tariff.prices.map((item) => (
          <div key={item.fromPeriod} style={{ borderBottom: '1px solid var(--line-soft)', paddingBottom: 8 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12 }}>
              <span className="strong">{formatPeriod(item.fromPeriod)}</span>
              <span className="mono">{formatPrice(item.price)} ₽</span>
            </div>
            <div className="small muted" style={{ marginTop: 2 }}>{item.who}, {item.when}{item.reason ? `. ${item.reason}` : ''}</div>
            {canEdit && item.fromPeriod > openPeriod && (
              <Button size="full" style={{ height: 32, marginTop: 6 }} disabled={!reason.trim()} title={reason.trim() ? undefined : 'Сначала укажите основание ниже'} onClick={() => void run(() => api.tariffs.cancelPrice(tariff.id, item.fromPeriod, reason.trim()), 'Цена отменена.')}>Отменить эту цену</Button>
            )}
          </div>
        ))}
        {canEdit ? (
          <>
            <h2 className="section-title" style={{ marginTop: 8 }}>Новая цена</h2>
            <Field label="Действует с месяца">{(id) => <TextInput id={id} type="month" min={openPeriod} value={fromPeriod} onChange={(event) => setFromPeriod(event.target.value)} />}</Field>
            <Field label="Цена за единицу, ₽">{(id) => <TextInput id={id} mono inputMode="decimal" value={price} onChange={(event) => setPrice(event.target.value)} />}</Field>
            <Field label="Основание">{(id) => <TextInput id={id} placeholder="Например, приказ № 12 от 01.11.2026" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
            <p className="small muted" style={{ lineHeight: 1.45 }}>{reopened ? 'Месяц открыт на исправление: новая цена попадёт в перезакрытие и будет действовать дальше до следующей записи.' : 'Прошлые месяцы и уже рассчитанные начисления цена не меняет.'}</p>
            {error && <Notice tone="warn">{error}</Notice>}
            {note && <Notice tone="ok">{note}</Notice>}
            <Button variant="primary" size="full" disabled={parsed === null || !reason.trim() || !fromPeriod} onClick={() => void run(() => api.tariffs.setPrice(tariff.id, { fromPeriod, price: parsed ?? 0, reason: reason.trim() }), 'Цена сохранена.')}>Сохранить цену</Button>
          </>
        ) : <p className="small muted">Цены меняет бухгалтер или администратор.</p>}
      </div>
    </aside>
  );
}
