import { useMemo, useState, type KeyboardEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, useApi } from '@/api';
import { Button, TextInput } from '@/ui';

interface Entry {
  group: string;
  title: string;
  hint: string;
  to: string;
}

const STATIC: Entry[] = [
  ...[
    ['Принять оплату', 'Касса', '/cash'], ['Вернуть деньги из кассы', 'Касса', '/cash'], ['Внести показания', 'Показания', '/readings'],
    ['Новый абонент', 'Абоненты', '/abonents/new'], ['Запустить начисление', 'Месяц', '/period'], ['Отменить начисления', 'Месяц', '/period'],
    ['Заблокировать начисления', 'Месяц', '/period'], ['Закрыть месяц', 'Месяц', '/period'], ['Перезакрыть период', 'Месяц', '/period'],
    ['Напечатать документы', 'Месяц', '/period'], ['Отменить платёж', 'Корректировки', '/corrections'], ['Перенести деньги', 'Корректировки', '/corrections'],
    ['Разовое начисление', 'Корректировки', '/corrections'], ['Списать остатки', 'Корректировки', '/corrections'], ['Загрузить реестр банка', 'Обмен', '/exchange'],
    ['Выгрузить в ГИС ЖКХ', 'Обмен', '/exchange'], ['Загрузить платежи 1С', 'Обмен', '/exchange'], ['Загрузить льготников', 'Обмен', '/exchange'],
  ].map(([title, hint, to]) => ({ group: 'Действия', title: title!, hint: hint!, to: to! })),
  ...['Должники', 'Обороты по кассе', 'Реестр по кассиру', 'Объёмы потребления', 'Потребление тепла', 'Обороты по тарифам', 'Реестр для банка'].map((title) => ({
    group: 'Отчёты', title, hint: 'отчёт', to: `/reports?report=${encodeURIComponent(title)}`,
  })),
  ...['Тарифы', 'Данные о домах', 'Люди', 'Котельные', 'Банк', 'Нормы по услугам', 'Услуги'].map((title) => ({
    group: 'Справочники', title, hint: 'справочник', to: `/directories?name=${encodeURIComponent(title)}`,
  })),
  ...[['Настройки пени', 'Месяц', '/period'], ['Офисы и кассы', 'Админ', '/admin'], ['Фоновые задачи', 'Админ', '/admin'], ['Сменить пароль', 'Профиль', '/admin']].map(([title, hint, to]) => ({
    group: 'Администрирование', title: title!, hint: hint!, to: to!,
  })),
];

const GROUPS = ['Абоненты', 'Действия', 'Отчёты', 'Справочники', 'Администрирование'];

export function CommandPalette({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate();
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const { data: abonents } = useApi(() => api.abonents.list(), []);

  const entries = useMemo<Entry[]>(() => {
    const people = (abonents ?? []).map((abonent) => ({
      group: 'Абоненты',
      title: abonent.name,
      hint: `ЛС ${abonent.account}, ${abonent.street}${abonent.flat ? `-${abonent.flat}` : ''}`,
      to: `/abonents?id=${abonent.id}`,
    }));
    const normalized = query.trim().toLowerCase();
    const matched = [...people, ...STATIC].filter((entry) => !normalized || `${entry.title} ${entry.hint}`.toLowerCase().includes(normalized));
    const limit = normalized ? 8 : 4;
    return GROUPS.flatMap((group) => matched.filter((entry) => entry.group === group).slice(0, limit));
  }, [abonents, query]);

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Escape') onClose();
    if (event.key === 'ArrowDown') { event.preventDefault(); setActive((value) => Math.min(value + 1, entries.length - 1)); }
    if (event.key === 'ArrowUp') { event.preventDefault(); setActive((value) => Math.max(value - 1, 0)); }
    if (event.key === 'Enter') {
      const entry = entries[active];
      if (entry) { navigate(entry.to); onClose(); }
    }
  };

  return (
    <div className="palette-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
      <div className="palette" role="dialog" aria-modal="true" aria-label="Общий поиск">
        <div className="palette-head">
          <TextInput
            autoFocus type="search" aria-label="Что найти" value={query} onKeyDown={onKeyDown}
            onChange={(event) => { setQuery(event.target.value); setActive(0); }}
            placeholder="Абонент, адрес, действие, отчёт или справочник"
          />
          <Button size="sm" onClick={onClose} style={{ height: 42 }}>Закрыть</Button>
        </div>
        <div className="scroll" style={{ padding: '4px 8px 12px' }}>
          {GROUPS.map((group) => {
            const rows = entries.filter((entry) => entry.group === group);
            if (rows.length === 0) return null;
            return (
              <div key={group}>
                <div className="palette-group">{group}</div>
                {rows.map((entry) => (
                  <Link key={`${group}-${entry.title}`} to={entry.to} onClick={onClose} className={`palette-row${entries[active] === entry ? ' is-active' : ''}`}>
                    <span>{entry.title}</span>
                    <span className="small muted nowrap">{entry.hint}</span>
                  </Link>
                ))}
              </div>
            );
          })}
          {entries.length === 0 && <p style={{ margin: '16px 8px' }} className="muted">Ничего не нашли. Проверьте написание.</p>}
        </div>
      </div>
    </div>
  );
}
