import { useEffect, useState } from 'react';
import { api, errorText, useApi, METER_UNITS, type HouseMeterRow } from '@/api';
import { useRole } from '@/layout/role';
import { Button, Field, LinkButton, Notice, SelectInput, TextInput } from '@/ui';

export function HouseMetersPage() {
  const role = useRole();
  const canEdit = role === 'service' || role === 'accountant' || role === 'admin';
  const { data } = useApi(() => api.readings.houseMeters(), []);
  const [values, setValues] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  // null — панель закрыта, 'new' — новый счётчик, иначе id изменяемого
  const [editing, setEditing] = useState<string | null>(null);

  useEffect(() => {
    if (data) setValues(Object.fromEntries(data.rows.map((row) => [row.id, row.value === null ? '' : String(row.value)])));
  }, [data]);

  const save = async (id: string, saved: number | null) => {
    const raw = (values[id] ?? '').trim().replace(',', '.');
    if (raw === '' || Number(raw) === saved) return;
    const value = Number(raw);
    if (!Number.isFinite(value)) {
      setErrors((current) => ({ ...current, [id]: 'Нужно число' }));
      return;
    }
    try {
      await api.readings.saveHouseMeter(id, value);
      setErrors((current) => ({ ...current, [id]: '' }));
    } catch (error) {
      setErrors((current) => ({ ...current, [id]: errorText(error) }));
    }
  };

  const rows = data?.rows ?? [];
  const filled = rows.filter((row) => row.value !== null).length;
  const editedRow = rows.find((row) => row.id === editing) ?? null;

  return (
    <div className="split">
      <main className="main">
        <div className="bar bar-tint" style={{ justifyContent: 'space-between' }}>
          <strong>Общедомовые счётчики{data ? `, ${data.period}` : ''}</strong>
          <div className="row">
            {canEdit && <Button active={editing === 'new'} onClick={() => setEditing(editing === 'new' ? null : 'new')}>Новый счётчик</Button>}
            <LinkButton to="/readings">К квартирным</LinkButton>
          </div>
        </div>
        <Notice>Расход делится по площади между работающими услугами на счётчике. Показания вносятся в единицах прибора (Гкал, ГДж, МВт·ч, кВт·ч), система переводит их в Гкал. Без показания услуги этого дома не начисляются.</Notice>
        <div className="scroll">
        <table className="table" style={{ minWidth: 900 }}>
          <thead>
            <tr><th>Адрес</th><th>Номер</th><th>Ед.</th><th className="right">Услуг</th><th className="right">Было</th><th>Показание</th><th className="right">Расход</th><th className="right">В Гкал</th><th>Проверка</th>{canEdit && <th />}</tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const entered = Number((values[row.id] ?? '').replace(',', '.'));
              const usage = values[row.id] && row.previous !== null && Number.isFinite(entered) ? entered - row.previous : null;
              return (
                <tr key={row.id} aria-selected={editing === row.id}>
                  <td>{row.place}, {row.street}</td>
                  <td className="mono">{row.number}</td>
                  <td>{row.unit}</td>
                  <td className="right mono">{row.lines}</td>
                  <td className="right mono">{row.previous ?? '—'}</td>
                  <td>
                    <TextInput
                      mono inputMode="decimal" readOnly={!canEdit} aria-label={`Показание счётчика ${row.number}`} value={values[row.id] ?? ''}
                      onChange={(event) => setValues((current) => ({ ...current, [row.id]: event.target.value }))}
                      onBlur={() => { if (canEdit) void save(row.id, row.value); }}
                    />
                  </td>
                  <td className="right mono">{usage === null ? '—' : usage.toFixed(3)}</td>
                  <td className="right mono">{usage === null ? '—' : (usage * row.unitFactor).toFixed(4)}</td>
                  <td className={`small ${errors[row.id] ? 'warn strong' : 'muted'}`}>{errors[row.id] || (usage !== null && usage < 0 ? 'Меньше предыдущего' : '')}</td>
                  {canEdit && <td><button type="button" className="link-button" onClick={() => setEditing(editing === row.id ? null : row.id)}>Изменить</button></td>}
                </tr>
              );
            })}
          </tbody>
        </table>
        </div>
        <div className="bar bar-tint" style={{ borderBottom: 0, borderTop: '1px solid var(--line)' }}>Внесено: <strong>{filled} из {rows.length}</strong></div>
      </main>
      {canEdit && editing !== null && (editing === 'new' || editedRow) && (
        <aside className="pane pane-right" aria-label="Общедомовой счётчик" style={{ width: 340 }}>
          <div className="scroll stack pad">
            <h2 className="section-title">{editing === 'new' ? 'Новый счётчик' : 'Счётчик'}</h2>
            <HouseMeterForm key={editing} meter={editedRow} onDone={() => setEditing(null)} />
          </div>
        </aside>
      )}
    </div>
  );
}

function HouseMeterForm({ meter, onDone }: { meter: HouseMeterRow | null; onDone: () => void }) {
  const [place, setPlace] = useState(meter?.place ?? '');
  const [street, setStreet] = useState(meter?.street ?? '');
  const [number, setNumber] = useState(meter?.number ?? '');
  const [unit, setUnit] = useState(meter?.unit ?? 'Гкал');
  const [error, setError] = useState<string | null>(null);
  const known = METER_UNITS.some((item) => item.value === unit);

  const submit = async () => {
    setError(null);
    const body = { place: place.trim(), street: street.trim(), number: number.trim(), unit };
    try {
      if (meter) await api.readings.editHouseMeter(meter.id, body);
      else await api.readings.createHouseMeter(body);
      onDone();
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  return (
    <>
      <Field label="Населённый пункт">{(id) => <TextInput id={id} placeholder="с. Новокрасное" value={place} onChange={(event) => setPlace(event.target.value)} />}</Field>
      <Field label="Улица и дом">{(id) => <TextInput id={id} placeholder="ул. Молодежная 1" value={street} onChange={(event) => setStreet(event.target.value)} />}</Field>
      <Field label="Номер прибора">{(id) => <TextInput id={id} mono value={number} onChange={(event) => setNumber(event.target.value)} />}</Field>
      <Field label="Единица показаний" hint="Тариф считается в Гкал, показания переводятся автоматически">{(id) => (
        <SelectInput id={id} value={unit} onChange={(event) => setUnit(event.target.value)}>
          {!known && <option value={unit}>{unit} (неизвестная, выберите)</option>}
          {METER_UNITS.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
        </SelectInput>
      )}</Field>
      {meter && meter.lines > 0 && <p className="small muted">К счётчику привязано услуг: {meter.lines}. Привязка делается в карточке абонента, вкладка «Жильцы и параметры».</p>}
      {error && <Notice tone="warn">{error}</Notice>}
      <Button variant="primary" size="full" disabled={!street.trim() || !known} onClick={() => void submit()}>{meter ? 'Сохранить' : 'Добавить счётчик'}</Button>
    </>
  );
}
