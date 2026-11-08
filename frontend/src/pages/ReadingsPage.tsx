import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { checkReading, parseReading, type ReadingCheck } from '@/domain/billing';
import { useRole } from '@/layout/role';
import { LinkButton, Notice, SelectInput, TextInput } from '@/ui';

type InputMode = 'current' | 'delta';

const CHECK_TEXT: Record<ReadingCheck, string> = { empty: 'Ждёт ввода', ok: 'В норме', lower: 'Меньше прошлого', high: 'Выше обычного' };
const CHECK_CLASS: Record<ReadingCheck, string> = { empty: 'muted', ok: 'ok', lower: 'warn strong', high: 'warn strong' };

export function ReadingsPage() {
  const { data: month } = useApi(() => api.period.current(), []);
  const role = useRole();
  const canEdit = role !== 'accountant';
  const { data: houses } = useApi(() => api.readings.houses(), []);
  // Из карточки абонента приходят дом и абонент: открываем его дом и подсвечиваем его счётчики
  const [params, setParams] = useSearchParams();
  const wantedHouse = params.get('house');
  const highlight = params.get('abonent');
  const [houseId, setHouseId] = useState<string | null>(wantedHouse);
  const selectHouse = (id: string) => { setHouseId(id); setParams({ house: id }, { replace: true }); };
  const [mode, setMode] = useState<InputMode>('current');
  const [filter, setFilter] = useState('');
  const activeHouse = houseId ?? houses?.[0]?.id ?? null;
  // Дома нет в списке: у него нет квартирных счётчиков. Чужой дом вместо него не открываем
  const missingHouse = Boolean(houses && activeHouse && !houses.some((item) => item.id === activeHouse));
  const { data: rows } = useApi(() => (activeHouse && !missingHouse ? api.readings.rows(activeHouse) : Promise.resolve([])), [activeHouse, missingHouse]);
  const [values, setValues] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});

  // Строка абонента может быть далеко внизу длинного дома: прокручиваем к ней один раз после загрузки
  useEffect(() => {
    if (rows && highlight) document.querySelector('tr[aria-current="true"]')?.scrollIntoView?.({ block: 'center' });
  }, [rows, highlight]);

  useEffect(() => {
    if (rows) {
      setValues(Object.fromEntries(rows.map((row) => [row.id, row.value])));
      setErrors({});
    }
  }, [rows]);

  const house = houses?.find((item) => item.id === activeHouse);
  const visibleHouses = (houses ?? []).filter((item) => !filter.trim() || item.name.toLowerCase().includes(filter.trim().toLowerCase()));

  const computed = (rows ?? []).map((row) => {
    const raw = values[row.id] ?? '';
    const entered = parseReading(raw);
    const next = entered === null ? null : mode === 'delta' ? row.previous + entered : entered;
    return { row, raw, next, usage: next === null ? null : next - row.previous, check: checkReading(row.previous, next, row.average) };
  });
  const filled = computed.filter((item) => item.next !== null).length;
  const problems = computed.filter((item) => item.check === 'lower' || item.check === 'high').length;

  const save = async (rowId: string, next: number | null, saved: string) => {
    if (next === null || String(next) === saved) return;
    try {
      await api.readings.save(rowId, next);
      setErrors(({ [rowId]: _cleared, ...rest }) => rest);
    } catch (reason) {
      setErrors((current) => ({ ...current, [rowId]: errorText(reason) }));
    }
  };

  return (
    <div className="split">
      <aside className="pane" style={{ width: 264 }}>
        <div style={{ padding: 12 }}>
          <TextInput type="search" aria-label="Найти дом" placeholder="Найти дом" value={filter} onChange={(event) => setFilter(event.target.value)} />
        </div>
        <div className="scroll" style={{ padding: '0 8px 12px' }}>
          {visibleHouses.map((item) => (
            <button key={item.id} type="button" className="list-item" style={{ borderBottom: 0, borderRadius: 5, padding: 8, marginBottom: 2 }} aria-pressed={item.id === activeHouse} onClick={() => selectHouse(item.id)}>
              <span className="between" style={{ fontSize: 13.5, fontWeight: 500 }}>
                <span>{item.name}</span>
                <span className="mono" style={{ fontSize: 12.5 }}>{item.done}/{item.total}</span>
              </span>
              <span className="progress"><span style={{ width: `${item.total ? Math.round((item.done / item.total) * 100) : 0}%` }} /></span>
            </button>
          ))}
        </div>
      </aside>

      <main className="main">
        <div className="bar">
          <h1 className="title">{house?.name ?? (missingHouse ? 'Дом без счётчиков' : 'Дом не выбран')}</h1>
          <span className="muted" style={{ fontSize: 13.5 }}>{month?.title.toLowerCase() ?? ''}</span>
          <div className="spacer" />
          <label className="small muted row">Ввожу
            <SelectInput aria-label="Что вводится" value={mode} onChange={(event) => setMode(event.target.value as InputMode)} style={{ width: 190, minHeight: 38, borderColor: mode === 'delta' ? 'var(--warn)' : undefined }}>
              <option value="current">Показание со счётчика</option>
              <option value="delta">Прирост за период</option>
            </SelectInput>
          </label>
          {role !== 'cashier' && <LinkButton to="/readings/import">Из файла</LinkButton>}
          {role !== 'cashier' && <LinkButton to="/readings/house">Общедомовые</LinkButton>}
        </div>
        {missingHouse && <div style={{ padding: '8px 16px' }}><Notice tone="warn">В доме абонента нет квартирных счётчиков, показания по нему не вводятся. Выберите дом слева.</Notice></div>}
        {!canEdit && <div style={{ padding: '8px 16px' }}><Notice>Для вашей роли показания доступны только для просмотра.</Notice></div>}
        {mode === 'delta' && (
          <div style={{ padding: '8px 16px' }}>
            <Notice tone="warn">Режим прироста: вводите только разницу. Текущее показание здесь завысит счётчик, смотрите колонку «Станет».</Notice>
          </div>
        )}
        <div className="scroll">
          <table className="table" style={{ minWidth: 800 }}>
            <thead>
              <tr><th>Кв.</th><th>Счётчик</th><th>Услуга</th><th className="right">Было</th><th>{mode === 'delta' ? 'Прирост' : 'Показание'}</th><th className="right">Станет</th><th className="right">Расход</th><th>Проверка</th></tr>
            </thead>
            <tbody>
              {computed.map(({ row, raw, next, usage, check }) => (
                <tr key={row.id} className={check === 'lower' || check === 'high' || errors[row.id] ? 'is-warn' : undefined} aria-current={highlight && row.id.startsWith(`${highlight}:`) ? 'true' : undefined} style={highlight && row.id.startsWith(`${highlight}:`) ? { background: 'var(--tint-strong)' } : undefined}>
                  <td className="strong">{row.flat}</td>
                  <td className="mono small">{row.meterNumber}</td>
                  <td>{row.service}</td>
                  <td className="right mono">{row.previous}</td>
                  <td>
                    <TextInput
                      mono inputMode="decimal" value={raw} readOnly={!canEdit} aria-label={`Значение для счётчика ${row.meterNumber}`} style={{ width: 96, minHeight: 34 }}
                      onChange={(event) => setValues((current) => ({ ...current, [row.id]: event.target.value }))}
                      onBlur={() => { if (canEdit) void save(row.id, next, row.value); }}
                    />
                  </td>
                  <td className="right mono">{next ?? '—'}</td>
                  <td className="right mono">{usage ?? '—'}</td>
                  <td className={`small ${errors[row.id] ? 'warn strong' : CHECK_CLASS[check]}`}>{errors[row.id] ?? `${CHECK_TEXT[check]}${check === 'high' ? `: обычно ${row.average}` : ''}`}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {computed.length === 0 && !missingHouse && <p className="pad muted">В этом доме нет счётчиков.</p>}
        </div>
        <div className="bar bar-tint" style={{ borderBottom: 0, borderTop: '1px solid var(--line)', justifyContent: 'space-between', fontSize: 13.5 }}>
          <span>Внесено: <strong>{filled} из {computed.length}</strong> · сохраняется при переходе на следующую строку</span>
          <span className={problems ? 'warn strong' : 'ok strong'}>{problems ? `Проверить: ${problems}` : 'Отклонений нет'}</span>
        </div>
      </main>
    </div>
  );
}
