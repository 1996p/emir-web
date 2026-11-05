import { useEffect, useState } from 'react';
import { api, errorText, useApi } from '@/api';
import { useRole } from '@/layout/role';
import { LinkButton, Notice, TextInput } from '@/ui';

export function HouseMetersPage() {
  const role = useRole();
  const canEdit = role === 'service' || role === 'accountant' || role === 'admin';
  const { data } = useApi(() => api.readings.houseMeters(), []);
  const [values, setValues] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});

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

  return (
    <main className="main">
      <div className="bar bar-tint" style={{ justifyContent: 'space-between' }}>
        <strong>Общедомовые счётчики{data ? `, ${data.period}` : ''}</strong>
        <LinkButton to="/readings">К квартирным</LinkButton>
      </div>
      <Notice>Расход делится по площади между работающими услугами на счётчике. Показания в единицах прибора, в Гкал система переводит сама. Без показания услуги этого дома не начисляются.</Notice>
      <div className="scroll">
      <table className="table" style={{ minWidth: 800 }}>
        <thead>
          <tr><th>Адрес</th><th>Номер</th><th>Ед.</th><th className="right">Услуг</th><th className="right">Было</th><th>Показание</th><th className="right">Расход</th><th>Проверка</th></tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            const entered = Number((values[row.id] ?? '').replace(',', '.'));
            const usage = values[row.id] && row.previous !== null && Number.isFinite(entered) ? entered - row.previous : null;
            return (
              <tr key={row.id}>
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
                <td className={`small ${errors[row.id] ? 'warn strong' : 'muted'}`}>{errors[row.id] || (usage !== null && usage < 0 ? 'Меньше предыдущего' : '')}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      </div>
      <div className="bar bar-tint" style={{ borderBottom: 0, borderTop: '1px solid var(--line)' }}>Внесено: <strong>{filled} из {rows.length}</strong></div>
    </main>
  );
}
