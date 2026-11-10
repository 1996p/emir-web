import { useState } from 'react';
import { api, errorText, useApi, type SaveTariffBody } from '@/api';
import { formatPeriod, formatPrice, nextPeriod, parsePrice } from '@/domain/tariffs';
import type { TariffItem } from '@/domain/types';
import { useRole } from '@/layout/role';
import { Button, DataTable, Field, Notice, SelectInput, TextInput, type Column } from '@/ui';

/** Схемы расчёта объёма. Сумма = объём × цена тарифа. */
export const SCHEMES: Record<string, string> = {
  NormHouse: 'Площадь × норма теплопотребления дома',
  ContractNorm: 'Норматив по заказу абонента',
  MeterApartment: 'По квартирному счётчику',
  MeterHouse: 'По общедомовому счётчику, делится по площади',
  HouseMeterLeftover: 'Остаток общедомового счётчика, по жильцам',
  Area: 'По площади',
  HeatHouseNorm: 'По площади (м²)',
  AreaNormTarif: 'Площадь × норматив по тарифу',
  AreaNormTarifMonthly: 'Площадь × норматив по тарифу (по месяцам)',
  AreaNormTarifSeasonal: 'Площадь × норматив по тарифу, только в отопительный сезон',
  NormTarif: 'Норматив по тарифу × жильцы',
  Norm: 'Норма по услуге × жильцы',
  ServiceNorm: 'Норма по услуге (фиксированный объём)',
  NormTarifContract: 'Норматив по тарифу × норматив по заказу',
  AreaKrskNorm: 'Норма по услуге × норматив по заказу',
  AreaKrsk: 'Норматив по заказу, иначе площадь',
  HeatHouseNormKrsk: 'Норматив по заказу, иначе по площади (тепло)',
  MonthlyAmount: 'Сумма по заказу на месяц, ₽',
  MonthlyQuantity: 'Объём по заказу на месяц',
  HeatLost: 'Тепловые потери: норматив по заказу × доля месяца',
  MonthlyQuantityHouse: 'Потери тепла дома, по площади',
};
const UNITS = ['Гкал', 'м²', 'м³', 'чел.', 'кВт·ч', 'шт.'];

export function TariffsDirectory({ filter }: { filter: string }) {
  const role = useRole();
  const { data, error } = useApi(() => api.tariffs.list(), []);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const canEdit = role === 'accountant' || role === 'admin';
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
        {canEdit && <div className="bar"><Button variant="primary" active={creating} onClick={() => setCreating((value) => !value)}>Новый тариф</Button></div>}
        {error && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{error}</Notice></div>}
        <DataTable columns={columns} rows={rows} rowKey={(tariff) => tariff.id} onRowClick={(tariff) => { setSelectedId(tariff.id); setCreating(false); }} isSelected={(tariff) => tariff.id === selected?.id} empty={<span className="muted">Тарифов нет.</span>} />
      </div>
      {creating && (
        <aside className="pane pane-right" aria-label="Новый тариф" style={{ width: 380 }}>
          <div className="scroll stack pad">
            <h2 className="section-title">Новый тариф</h2>
            <TariffForm openPeriod={open} onSaved={(id) => { setCreating(false); setSelectedId(id); }} />
          </div>
        </aside>
      )}
      {!creating && selected && <TariffPanel key={`${selected.id}-${open}-${data?.reopened}`} tariff={selected} openPeriod={open} reopened={data?.reopened ?? false} canEdit={canEdit} />}
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
        <h2 className="section-title">{tariff.name}</h2>
        <p className="small muted">{SCHEMES[tariff.scheme] ?? tariff.scheme}{tariff.unit ? `, ${tariff.unit}` : ''}{tariff.tariffNorm ? `, норматив ${tariff.tariffNorm}` : ''}{tariff.serviceNorm ? `, норма ${tariff.serviceNorm}` : ''}</p>
        {canEdit && <details className="card" style={{ padding: 10 }}><summary className="strong" style={{ cursor: 'pointer' }}>Изменить тариф</summary><div className="stack" style={{ marginTop: 10 }}><TariffForm tariff={tariff} openPeriod={openPeriod} /></div></details>}
        <h2 className="section-title">История цен</h2>
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

/** Форма тарифа: новый (без tariff) или правка существующего. */
function TariffForm({ tariff, openPeriod, onSaved }: { tariff?: TariffItem; openPeriod: string; onSaved?: (id: string) => void }) {
  const [name, setName] = useState(tariff?.name ?? '');
  const [scheme, setScheme] = useState(tariff?.scheme ?? 'NormHouse');
  const [unit, setUnit] = useState(tariff?.unit ?? 'Гкал');
  const [tariffNorm, setTariffNorm] = useState(tariff?.tariffNorm ?? '');
  const [serviceNorm, setServiceNorm] = useState(tariff?.serviceNorm ?? '');
  const [penalty, setPenalty] = useState(tariff?.penaltyEnabled ?? true);
  const [price, setPrice] = useState('');
  const [fromPeriod, setFromPeriod] = useState(openPeriod);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const parsedPrice = price.trim() === '' ? null : parsePrice(price);
  const valid = name.trim() !== '' && reason.trim() !== '' && (price.trim() === '' || parsedPrice !== null);
  const needsTariffNorm = /NormTarif/.test(scheme);
  const needsServiceNorm = scheme === 'Norm' || scheme === 'ServiceNorm' || scheme === 'AreaKrskNorm';

  const save = async () => {
    setError(null);
    setNote(null);
    const body: SaveTariffBody = { name: name.trim(), scheme, unit: unit.trim() || null, tariffNorm: tariffNorm.trim() || null, serviceNorm: serviceNorm.trim() || null, penaltyEnabled: penalty, reason: reason.trim() };
    try {
      if (tariff) {
        await api.tariffs.update(tariff.id, body);
        setNote('Тариф сохранён. Изменения учтутся при следующем начислении.');
      } else {
        const created = await api.tariffs.create({ ...body, price: parsedPrice, fromPeriod: parsedPrice === null ? null : fromPeriod });
        onSaved?.(created.id);
      }
      setReason('');
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <>
      <Field label="Название">{(id) => <TextInput id={id} placeholder="Например, теплоснабжение (с. Новокрасное)" value={name} onChange={(event) => setName(event.target.value)} />}</Field>
      <Field label="Схема расчёта объёма">{(id) => (
        <SelectInput id={id} value={scheme} onChange={(event) => setScheme(event.target.value)}>
          {Object.entries(SCHEMES).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </SelectInput>
      )}</Field>
      <Field label="Единица">{(id) => (
        <SelectInput id={id} value={unit} onChange={(event) => setUnit(event.target.value)}>
          {[...new Set([...UNITS, unit])].map((value) => <option key={value} value={value}>{value}</option>)}
        </SelectInput>
      )}</Field>
      {(needsTariffNorm || tariffNorm) && <Field label="Норматив по тарифу" hint="Число или по месяцам: 01=0,03#02=0,03">{(id) => <TextInput id={id} mono value={tariffNorm} onChange={(event) => setTariffNorm(event.target.value)} />}</Field>}
      {(needsServiceNorm || serviceNorm) && <Field label="Норма по услуге" hint="Число или по месяцам: 01=0,03#02=0,03">{(id) => <TextInput id={id} mono value={serviceNorm} onChange={(event) => setServiceNorm(event.target.value)} />}</Field>}
      <label className="row" style={{ gap: 8 }}><input type="checkbox" checked={penalty} onChange={(event) => setPenalty(event.target.checked)} />Начислять пени</label>
      {!tariff && (
        <div className="row" style={{ alignItems: 'flex-start' }}>
          <Field label="Цена за единицу, ₽">{(id) => <TextInput id={id} mono inputMode="decimal" placeholder="можно позже" value={price} onChange={(event) => setPrice(event.target.value)} />}</Field>
          <Field label="С месяца">{(id) => <TextInput id={id} type="month" value={fromPeriod} onChange={(event) => setFromPeriod(event.target.value)} />}</Field>
        </div>
      )}
      <Field label="Основание">{(id) => <TextInput id={id} placeholder="Например, приказ № 12 от 01.11.2026" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
      {tariff && tariff.lines > 0 && <p className="small muted">Тариф у {tariff.lines} услуг абонентов. Новая схема и нормативы учтутся при следующем начислении, закрытые месяцы не меняются.</p>}
      {error && <Notice tone="warn">{error}</Notice>}
      {note && <Notice tone="ok">{note}</Notice>}
      <Button variant="primary" size="full" disabled={!valid} onClick={() => void save()}>{tariff ? 'Сохранить тариф' : 'Создать тариф'}</Button>
    </>
  );
}
