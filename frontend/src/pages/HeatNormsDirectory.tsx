import { useState } from 'react';
import { api, errorText, useApi } from '@/api';
import type { HeatNormRow } from '@/domain/types';
import { useRole } from '@/layout/role';
import { Button, DataTable, Field, Notice, TextInput, type Column } from '@/ui';

/** Норма теплопотребления Гкал/м² у абонентов категории. Принимает «0,0188» и «0.0188». */
function parseNorm(value: string): number | null {
  const normalized = value.trim().replace(',', '.');
  if (!/^\d*\.?\d+$/.test(normalized)) return null;
  const parsed = Number(normalized);
  return Number.isFinite(parsed) && parsed > 0 && parsed <= 10 ? parsed : null;
}

const formatNorm = (value: number) => value.toLocaleString('ru-RU', { maximumFractionDigits: 6 });

export function HeatNormsDirectory({ filter }: { filter: string }) {
  const role = useRole();
  const { data, error } = useApi(() => api.heatNorms.list(), []);
  const [selected, setSelected] = useState<string | null>(null);
  const canEdit = role === 'accountant' || role === 'admin';
  const needle = filter.trim().toLowerCase();
  const rows = (data?.categories ?? []).filter((row) => !needle || row.category.toLowerCase().includes(needle));
  const current = rows.find((row) => row.category === selected) ?? rows[0];

  const columns: Column<HeatNormRow>[] = [
    { key: 'category', header: 'Категория дома', render: (row) => row.inDirectory === false ? <span>{row.category} <span className="muted">(вне справочника)</span></span> : row.category },
    { key: 'abonents', header: 'Абонентов', render: (row) => row.abonents },
    { key: 'norm', header: 'Норма категории, Гкал/м²', render: (row) => row.norm === null ? <span className="muted">не задана</span> : formatNorm(row.norm) },
    {
      key: 'distinct', header: 'Стоит у абонентов', render: (row) =>
        row.distinct.length === 0 ? <span className="muted">нет</span>
          : row.distinct.length === 1 ? formatNorm(row.distinct[0] ?? 0)
            : <span style={{ color: 'var(--warn, #b45309)' }}>{row.distinct.map(formatNorm).join(', ')}</span>,
    },
    { key: 'without', header: 'Без нормы', render: (row) => row.withoutNorm === 0 ? '—' : <span style={{ color: 'var(--warn, #b45309)' }}>{row.withoutNorm}</span> },
  ];

  return (
    <div className="split">
      <div className="main">
        {error && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{error}</Notice></div>}
        {data && data.withoutCategory > 0 && (
          <div style={{ padding: '8px 16px' }}>
            <Notice>У {data.withoutCategory.toLocaleString('ru-RU')} абонентов не указана категория дома — задать им норму здесь нельзя. Укажите категорию в карточке абонента.</Notice>
          </div>
        )}
        <DataTable columns={columns} rows={rows} rowKey={(row) => row.category} onRowClick={(row) => setSelected(row.category)} isSelected={(row) => row.category === current?.category} empty={<span className="muted">Категорий домов нет. Заведите их в справочнике «Категории домов».</span>} />
      </div>
      {current && (
        <aside className="pane pane-right" aria-label="Норма категории" style={{ width: 360 }}>
          <div className="scroll stack pad">
            <HeatNormPanel key={current.category} row={current} canEdit={canEdit} />
          </div>
        </aside>
      )}
    </div>
  );
}

function HeatNormPanel({ row, canEdit }: { row: HeatNormRow; canEdit: boolean }) {
  const [norm, setNorm] = useState(row.norm !== null ? formatNorm(row.norm) : (row.distinct.length === 1 ? formatNorm(row.distinct[0] ?? 0) : ''));
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const parsed = parseNorm(norm);

  const apply = async () => {
    setError(null);
    setNote(null);
    try {
      const result = await api.heatNorms.apply({ category: row.category, norm: parsed ?? 0, reason: reason.trim() });
      setNote(`Норма проставлена ${result.changed} из ${result.total} абонентов. Начисления пересчитаются при следующем расчёте.`);
      setReason('');
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <>
      <h2 className="section-title">{row.category}</h2>
      {row.inDirectory === false && <Notice tone="warn">Этой категории нет в справочнике «Категории домов». Заведите её там или смените категорию абонентам в карточке.</Notice>}
      <p className="small muted" style={{ lineHeight: 1.45 }}>
        Абонентов в категории: {row.abonents}. Без нормы: {row.withoutNorm}.
        {row.distinct.length > 1 && ' Сейчас у абонентов стоят разные нормы — применение выровняет их.'}
      </p>
      {canEdit ? (
        <>
          <Field label="Норма теплопотребления, Гкал/м² в месяц" hint="Например, 0,0188">{(id) => <TextInput id={id} mono inputMode="decimal" value={norm} onChange={(event) => setNorm(event.target.value)} />}</Field>
          <Field label="Основание">{(id) => <TextInput id={id} placeholder="Например, приказ № 12 от 01.11.2026" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
          <p className="small muted" style={{ lineHeight: 1.45 }}>Норма запишется в справочник «Категории домов» и проставится всем {row.abonents} абонентам этой категории. Закрытые месяцы не пересчитываются.</p>
          {error && <Notice tone="warn">{error}</Notice>}
          {note && <Notice tone="ok">{note}</Notice>}
          <Button variant="primary" size="full" disabled={parsed === null || !reason.trim() || row.inDirectory === false} onClick={() => void apply()}>Применить ко всем в категории</Button>
        </>
      ) : <p className="small muted">Норму задаёт бухгалтер или администратор.</p>}
    </>
  );
}
