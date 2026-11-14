import { useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { balanceNote, balanceOf, chargeForReading, formatBalance, formatMoney } from '@/domain/billing';
import type { Abonent, AddressValue } from '@/domain/types';
import { AddressPicker, addressOf, formatAddress, isAddressComplete } from './AddressPicker';
import { useRole } from '@/layout/role';
import { formatPeriod, formatPrice, nextPeriod } from '@/domain/tariffs';
import { AbonentDetailsTab, AbonentNote } from './AbonentDetails';
import { Button, DataTable, Field, LinkButton, Notice, SelectInput, Tabs, TextInput, type Column } from '@/ui';

type View = 'Абоненты' | 'Дома' | 'Контакты' | 'Счета' | 'Заказы' | 'Группы услуг' | 'Карта';
type Chip = 'Все' | 'Долг' | 'Юрлица' | 'Нет тел.';
type Tab = 'services' | 'charges' | 'money' | 'payments' | 'meters' | 'contract' | 'journal';
type Drawer = 'charge' | 'service' | 'terms' | 'address' | 'docs';

const VIEWS: Array<[View, string]> = [['Абоненты', 'Абоненты'], ['Дома', 'Дома'], ['Контакты', 'Контакты'], ['Счета', 'Счета'], ['Заказы', 'Заказы'], ['Группы услуг', 'Группы'], ['Карта', 'Карта']];
const CHIPS: Chip[] = ['Все', 'Долг', 'Юрлица', 'Нет тел.'];
const VIEW_TAB: Partial<Record<View, Tab>> = { 'Контакты': 'contract', 'Счета': 'charges', 'Заказы': 'services', 'Группы услуг': 'services', 'Абоненты': 'services' };

/** Экран показаний сразу на доме абонента и с его строками. */
const readingsLink = (abonent?: Abonent, highlight = true) => (abonent?.houseKey ? `/readings?${new URLSearchParams(highlight ? { house: abonent.houseKey, abonent: abonent.id } : { house: abonent.houseKey }).toString()}` : '/readings');

const fullAddress = (abonent: Abonent) => `${abonent.place ? `${abonent.place}, ` : ''}${abonent.street}${abonent.flat ? `-${abonent.flat}` : ''}`;

function subline(abonent: Abonent, view: View): string {
  switch (view) {
    case 'Контакты': return abonent.phone ?? 'телефона нет';
    case 'Счета': return `ЛС ${abonent.account}`;
    case 'Заказы': return abonent.services.map((line) => line.name).join(', ');
    case 'Группы услуг': return `${abonent.services[0]?.name ?? 'Нет услуг'} · ${abonent.area} м²`;
    default: return fullAddress(abonent);
  }
}

export function AbonentsPage() {
  const { data: abonents } = useApi(() => api.abonents.list(), []);
  const [params, setParams] = useSearchParams();
  const [query, setQuery] = useState(() => params.get('q') ?? '');
  const [chip, setChip] = useState<Chip>('Все');
  const [view, setView] = useState<View>('Абоненты');
  const [tab, setTab] = useState<Tab>('services');
  const [houseKey, setHouseKey] = useState<string | null>(null);
  const [drawer, setDrawer] = useState<Drawer | null>(null);
  const [note, setNote] = useState<string | null>(null);

  const selectedId = params.get('id');
  const select = (id: string) => { setParams({ id }, { replace: true }); setDrawer(null); setNote(null); };

  const filtered = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    return (abonents ?? []).filter((abonent) => {
      const text = `${abonent.name} ${abonent.shortName} ${fullAddress(abonent)} ${abonent.account} ${abonent.phone ?? ''}`.toLowerCase();
      if (normalized && !text.includes(normalized)) return false;
      if (chip === 'Долг') return balanceOf(abonent) < 0;
      if (chip === 'Юрлица') return abonent.kind === 'org';
      if (chip === 'Нет тел.') return !abonent.phone;
      return true;
    });
  }, [abonents, query, chip]);

  const houses = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    const map = new Map<string, Abonent[]>();
    (abonents ?? []).forEach((abonent) => {
      const key = abonent.houseKey ?? `${abonent.place}|${abonent.street}`;
      map.set(key, [...(map.get(key) ?? []), abonent]);
    });
    return [...map.entries()].map(([key, people]) => ({ key, people })).filter((house) => !normalized || house.key.toLowerCase().includes(normalized));
  }, [abonents, query]);

  const houseMode = view === 'Дома' || view === 'Карта';
  const selected = filtered.find((abonent) => abonent.id === selectedId) ?? filtered[0] ?? null;
  const house = houses.find((item) => item.key === houseKey) ?? houses[0] ?? null;

  const changeView = (next: View) => {
    setView(next);
    const nextTab = VIEW_TAB[next];
    if (nextTab) setTab(nextTab);
    setDrawer(null);
    setNote(null);
  };

  return (
    <div className="split">
      <aside className="pane" style={{ width: 372 }}>
        <div className="stack" style={{ padding: 12, gap: 8, borderBottom: '1px solid var(--line)' }}>
          <div style={{ display: 'flex', gap: 6 }}>
            <TextInput primary type="search" aria-label="Поиск" value={query} onChange={(event) => setQuery(event.target.value)} placeholder={houseMode ? 'Улица или дом' : 'ФИО, ЛС, адрес, телефон'} />
            <LinkButton to="/abonents/new" size="auto"><span style={{ width: 56, textAlign: 'center' }}>Новый</span></LinkButton>
          </div>
          <div role="group" aria-label="Вид списка" style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 4 }}>
            {VIEWS.map(([value, label]) => (
              <button key={value} type="button" aria-pressed={view === value} onClick={() => changeView(value)} style={{ height: 32, padding: '0 2px', border: 0, borderRadius: 5, fontSize: 12.5, fontWeight: 600, background: view === value ? 'var(--tint-strong)' : 'transparent', color: view === value ? 'var(--brand-dark)' : 'var(--ink-2)' }}>
                {label}
              </button>
            ))}
          </div>
          {!houseMode && (
            <div role="group" aria-label="Быстрые фильтры" style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 4 }}>
              {CHIPS.map((value) => (
                <button key={value} type="button" aria-pressed={chip === value} onClick={() => setChip(value)} style={{ height: 34, padding: '0 2px', borderRadius: 6, fontSize: 12.5, fontWeight: 500, border: `1px solid ${chip === value ? 'var(--brand)' : 'var(--control)'}`, background: chip === value ? 'var(--brand)' : 'var(--surface)', color: chip === value ? '#fff' : 'var(--ink)' }}>
                  {value}
                </button>
              ))}
            </div>
          )}
          <div className="small muted right">Найдено: {houseMode ? houses.length : filtered.length}</div>
        </div>
        <div className="scroll">
          {!houseMode && filtered.map((abonent) => {
            const balance = balanceOf(abonent);
            return (
              <button key={abonent.id} type="button" className="list-item" aria-pressed={selected?.id === abonent.id} onClick={() => select(abonent.id)}>
                <span className="between">
                  <span className="list-title">{abonent.shortName}</span>
                  <span className={`mono small nowrap strong${balance < 0 ? ' warn' : ''}`}>{formatBalance(balance)}</span>
                </span>
                <span className="list-sub">{subline(abonent, view)}</span>
              </button>
            );
          })}
          {houseMode && houses.map((item) => (
            <button key={item.key} type="button" className="list-item" aria-pressed={house?.key === item.key} onClick={() => setHouseKey(item.key)}>
              <span className="between">
                <span className="list-title">{item.people[0]?.street}</span>
                <span className="mono small">{item.people.length} аб.</span>
              </span>
              <span className="list-sub">{item.people[0]?.place}</span>
            </button>
          ))}
          {!houseMode && filtered.length === 0 && (
            <div className="pad" style={{ lineHeight: 1.5 }}><strong>Ничего не нашли.</strong><br />Проверьте написание или заведите абонента.</div>
          )}
        </div>
      </aside>

      {view === 'Карта' ? (
        <main className="main pad" style={{ gap: 10 }}>
          <h1 style={{ fontSize: 22, fontWeight: 700 }}>Карта обслуживания</h1>
          <div className="muted" style={{ flex: '1 1 auto', minHeight: 200, background: 'var(--tint)', border: '1px dashed #8c98a3', borderRadius: 8, display: 'flex', alignItems: 'center', justifyContent: 'center', textAlign: 'center', padding: 24, lineHeight: 1.5 }}>
            [Карта с домами, котельными и поставщиком]<br />На неё попадают объекты, у которых заданы координаты.
          </div>
        </main>
      ) : view === 'Дома' ? (
        <main className="main">
          <div className="bar" style={{ justifyContent: 'space-between' }}>
            <div>
              <h1 style={{ fontSize: 22, fontWeight: 700 }}>{house?.people[0]?.street ?? 'Дом не выбран'}</h1>
              <p className="small muted" style={{ marginTop: 3 }}>{house?.people[0]?.place} · абонентов: {house?.people.length ?? 0}</p>
            </div>
            <div className="row">
              <LinkButton to={readingsLink(house?.people[0], false)}>Показания</LinkButton>
              <LinkButton to="/directories?name=Данные о домах">Данные дома</LinkButton>
            </div>
          </div>
          <div className="scroll" style={{ padding: '8px 12px' }}>
            {house?.people.map((person) => (
              <button key={person.id} type="button" onClick={() => { changeView('Абоненты'); select(person.id); }} style={{ display: 'flex', justifyContent: 'space-between', gap: 16, width: '100%', minHeight: 44, padding: 8, textAlign: 'left', background: 'transparent', border: 0, borderBottom: '1px solid var(--line-soft)', fontWeight: 500 }}>
                <span>{person.shortName}{person.flat ? `, кв. ${person.flat}` : ''}</span>
                <span className={`mono${balanceOf(person) < 0 ? ' warn' : ''}`}>{formatBalance(balanceOf(person))}</span>
              </button>
            ))}
          </div>
        </main>
      ) : selected ? (
        <AbonentCard key={selected.id} abonent={selected} tab={tab} onTab={setTab} drawer={drawer} onDrawer={setDrawer} note={note} onNote={setNote} />
      ) : (
        <main className="main muted" style={{ alignItems: 'center', justifyContent: 'center', fontSize: 15 }}>Найдите и выберите абонента слева.</main>
      )}
    </div>
  );
}

interface CardProps {
  abonent: Abonent;
  tab: Tab;
  onTab: (tab: Tab) => void;
  drawer: Drawer | null;
  onDrawer: (drawer: Drawer | null) => void;
  note: string | null;
  onNote: (note: string | null) => void;
}

interface ServiceCell { key: string; cells: string[] }

function AbonentCard({ abonent, tab, onTab, drawer, onDrawer, note, onNote }: CardProps) {
  const navigate = useNavigate();
  const role = useRole();
  const { data: payments } = useApi(() => api.abonents.payments(abonent.id), [abonent.id]);
  const { data: journal } = useApi(() => api.abonents.journal(abonent.id), [abonent.id]);
  const { data: charges } = useApi(() => api.abonents.charges(abonent.id), [abonent.id]);
  const { data: ledger } = useApi(() => api.abonents.ledger(abonent.id), [abonent.id]);
  const { data: details } = useApi(() => api.abonents.details(abonent.id), [abonent.id]);
  const [showEnded, setShowEnded] = useState(false);
  const workPeriod = details?.openPeriod;
  const volumeOf = (service: string) => {
    const entry = (charges ?? []).find((item) => item.period === workPeriod && item.service === service);
    return entry?.volume ? String(entry.volume) : '—';
  };
  const visibleServices = abonent.services.filter((line) => showEnded || line.state !== 'Расторгнута');
  const endedCount = abonent.services.length - abonent.services.filter((line) => line.state !== 'Расторгнута').length;
  const balance = balanceOf(abonent);
  const penalty = abonent.services.find((line) => line.id === 'penalty')?.debt ?? 0;
  const isOrg = abonent.kind === 'org';
  const tabs: Array<{ value: Tab; label: string }> = [
    { value: 'services', label: 'Услуги' }, { value: 'charges', label: 'Начисления' }, { value: 'money', label: 'Движение денег' }, { value: 'payments', label: 'Оплаты' },
    { value: 'meters', label: 'Счётчики' }, { value: 'contract', label: isOrg ? 'Реквизиты и параметры' : 'Жильцы и параметры' }, { value: 'journal', label: 'Журнал' },
  ];

  const tables: Record<Tab, { columns: string[]; rows: string[][] }> = {
    services: { columns: ['Услуга', 'Состояние', 'Тариф', 'Цена, ₽', 'Объём за месяц', 'Начисление', 'Долг, ₽'], rows: visibleServices.map((line) => [line.name, line.state, line.tariffName, line.price === null ? '—' : formatPrice(line.price), volumeOf(line.name), line.chargeStatus, formatMoney(line.debt)]) },
    charges: { columns: ['Месяц', 'Услуга', 'Объём', 'Схема', 'Цена, ₽', 'Сумма, ₽'], rows: (charges ?? []).map((entry) => [entry.period, entry.service, entry.volume ? String(entry.volume) : '—', entry.scheme === 'Opening' ? 'Входящий долг' : entry.scheme, entry.price === null ? '—' : formatPrice(entry.price), formatMoney(entry.amount)]) },
    money: { columns: ['Когда', 'Операция', 'Строка', 'Изменение, ₽', 'Остаток, ₽', 'Кто', 'Основание'], rows: (ledger ?? []).map((row) => [row.when, row.kindName, row.service, formatBalance(row.creditDelta - row.debtDelta), formatBalance(row.balanceAfter), row.who, row.reference ?? '—']) },
    payments: { columns: ['Дата', 'Источник', 'Кассир', 'Статус', 'Сумма, ₽'], rows: (payments ?? []).map((payment) => [payment.date, payment.source, payment.cashier, payment.status, formatMoney(payment.amount)]) },
    meters: { columns: ['Номер', 'Услуга', 'Ед. показаний', 'Поверка', 'Последнее показание', 'Тариф за единицу, ₽'], rows: abonent.services.filter((line) => line.meter).map((line) => [line.meter!.number, line.name, line.meter!.unit ? `${line.meter!.unit} → Гкал` : 'единицы тарифа', `до ${line.meter!.verifiedUntil}`, String(line.meter!.lastReading), formatMoney(line.meter!.tariff)]) },
    contract: { columns: [], rows: [] },
    journal: { columns: ['Когда', 'Действие', 'Кто', 'Что изменилось', 'Причина'], rows: (journal ?? []).map((record) => [record.when, record.action, record.who, record.details, record.reason ?? '—']) },
  };
  const table = tables[tab];
  const columns: Column<ServiceCell>[] = table.columns.map((header, index) => ({ key: header, header, render: (row) => row.cells[index] }));
  const rows: ServiceCell[] = table.rows.map((cells, index) => ({ key: String(index), cells }));
  const emptyText = tab === 'meters' ? 'Счётчиков нет: услуги начисляются по нормативу.' : 'Записей нет.';

  const meta = isOrg
    ? `Юрлицо · ИНН ${abonent.inn} · ЛС ${abonent.account} · ${abonent.phone ?? 'телефона нет'} · ${abonent.area} м²`
    : `Физлицо · ЛС ${abonent.account} · ${abonent.phone ?? 'телефона нет'} · проживает ${abonent.residents} · ${abonent.area} м²`;

  const toggle = (next: Drawer) => { onDrawer(drawer === next ? null : next); onNote(null); };

  return (
    <div className="split" style={{ flex: '1 1 auto', minWidth: 0 }}>
      <main className="main">
        <header className="bar" style={{ alignItems: 'flex-start', justifyContent: 'space-between', gap: '10px 24px' }}>
          <div style={{ flex: '1 1 360px', minWidth: 0 }}>
            <h1 style={{ fontSize: 22, fontWeight: 700 }}>{abonent.name}</h1>
            <p className="muted" style={{ marginTop: 3 }}>{fullAddress(abonent)}</p>
            <p className="small muted" style={{ marginTop: 3 }}>{meta}</p>
          </div>
          <div className={`right${balance < 0 ? ' warn' : ''}`}>
            <div className="mono" style={{ fontSize: 26, fontWeight: 500 }}>{formatBalance(balance)} ₽</div>
            <div style={{ fontSize: 12.5 }}>{balanceNote(balance, penalty)}</div>
          </div>
        </header>
        <div className="bar" style={{ gap: 6 }}>
          {(role === 'cashier' || role === 'admin') && <Button variant="primary" onClick={() => navigate(`/cash?id=${abonent.id}`)}>Принять оплату</Button>}
          {abonent.services.some((line) => line.meter)
            ? <LinkButton to={readingsLink(abonent)}>Показания</LinkButton>
            : <Button disabled title="У абонента нет квартирных счётчиков: показания не вводятся">Показания</Button>}
          {role !== 'cashier' && <Button active={drawer === 'charge'} aria-expanded={drawer === 'charge'} onClick={() => toggle('charge')}>Начислить</Button>}
          {(role === 'service' || role === 'admin') && <Button active={drawer === 'service'} aria-expanded={drawer === 'service'} onClick={() => toggle('service')}>Добавить услугу</Button>}
          {role !== 'cashier' && <Button active={drawer === 'terms'} aria-expanded={drawer === 'terms'} onClick={() => toggle('terms')}>Тариф услуги</Button>}
          <Button active={drawer === 'address'} aria-expanded={drawer === 'address'} onClick={() => toggle('address')}>Сменить адрес</Button>
          <Button active={drawer === 'docs'} aria-expanded={drawer === 'docs'} onClick={() => toggle('docs')}>Документы</Button>
          <a className="btn btn-md" target="_blank" rel="noreferrer" href={`/print/abonent/${encodeURIComponent(abonent.id)}`}>Справка</a>
          {role !== 'service' && <LinkButton to={`/corrections?id=${abonent.id}`}>Корректировка</LinkButton>}
        </div>
        {note && <div style={{ padding: '8px 16px' }}><Notice tone="ok">{note}</Notice></div>}
        {details && <AbonentNote abonent={abonent} note={details.note} />}
        <Tabs label="Разделы карточки" items={tabs} value={tab} onChange={onTab} />
        {tab === 'services' && endedCount > 0 && (
          <label className="small row" style={{ padding: '8px 16px', gap: 6 }}><input type="checkbox" checked={showEnded} onChange={(event) => setShowEnded(event.target.checked)} />Показывать расторгнутые: {endedCount}</label>
        )}
        {tab === 'contract'
          ? <AbonentDetailsTab abonent={abonent} details={details ?? null} />
          : <DataTable columns={columns} rows={rows} rowKey={(row) => row.key} minWidth={680} empty={<span className="muted">{emptyText}</span>} />}
      </main>
      {drawer === 'terms' && <TermsDrawer abonent={abonent} onClose={() => onDrawer(null)} onDone={(text) => { onDrawer(null); onNote(text); }} />}
      {drawer && drawer !== 'terms' && <ActionDrawer abonent={abonent} kind={drawer} onClose={() => onDrawer(null)} onDone={(text) => { onDrawer(null); onNote(text); }} />}
    </div>
  );
}

const SERVICE_STATES = ['Работает', 'Приостановлена', 'Расторгнута'];

function TermsDrawer({ abonent, onClose, onDone }: { abonent: Abonent; onClose: () => void; onDone: (text: string) => void }) {
  const lines = abonent.services.filter((line) => line.id !== 'penalty');
  const [serviceId, setServiceId] = useState(lines[0]?.id ?? '');
  const line = lines.find((item) => item.id === serviceId);
  const { data: tariffs } = useApi(() => api.tariffs.list(), []);
  const { data: terms } = useApi(() => (serviceId ? api.abonents.terms(abonent.id, serviceId) : Promise.resolve([])), [abonent.id, serviceId]);
  const open = tariffs?.openPeriod ?? '';
  const [fromPeriod, setFromPeriod] = useState<string | null>(null);
  const [tariffId, setTariffId] = useState<string | null>(null);
  const [state, setState] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const month = fromPeriod ?? (tariffs?.reopened ? open : nextPeriod(open));
  const latest = terms?.[0];
  const chosenTariff = tariffId ?? latest?.tariffId ?? '';
  const chosenState = state ?? latest?.state ?? 'Работает';
  // Смена услуги на одном ЛС: эта услуга закрывается, долг переходит на выбранную
  const targets = lines.filter((item) => item.id !== serviceId && item.state !== 'Расторгнута');
  const [replaceWith, setReplaceWith] = useState('');
  const target = targets.find((item) => item.id === replaceWith) ?? targets[0];

  const replace = async () => {
    if (!line || !target) return;
    setError(null);
    try {
      await api.abonents.replaceService(abonent.id, serviceId, target.id, reason.trim());
      onDone(`Услуга «${line.name}» закрыта, долг ${formatMoney(line.debt)} ₽ перенесён на «${target.name}».`);
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  const submit = async () => {
    setError(null);
    try {
      await api.abonents.setTerm(abonent.id, serviceId, { fromPeriod: month, tariffId: chosenTariff || null, state: chosenState, reason: reason.trim() });
      onDone(`Условия услуги «${line?.name ?? ''}» сохранены с месяца: ${formatPeriod(month)}.`);
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <aside className="pane pane-right" aria-label="Тариф услуги" style={{ width: 340, background: 'var(--surface)' }}>
      <div className="bar" style={{ justifyContent: 'space-between' }}>
        <h2 style={{ fontSize: 17, fontWeight: 600 }}>Тариф услуги</h2>
        <Button size="sm" onClick={onClose}>Закрыть</Button>
      </div>
      <div className="scroll stack pad">
        <Field label="Услуга">{(id) => <SelectInput id={id} value={serviceId} onChange={(event) => { setServiceId(event.target.value); setTariffId(null); setState(null); }}>{lines.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</SelectInput>}</Field>
        <Field label="Действует с месяца">{(id) => <TextInput id={id} type="month" min={open} value={month} onChange={(event) => setFromPeriod(event.target.value)} />}</Field>
        <Field label="Тариф">{(id) => <SelectInput id={id} value={chosenTariff} onChange={(event) => setTariffId(event.target.value)}><option value="">Не выбран</option>{tariffs?.tariffs.map((tariff) => <option key={tariff.id} value={tariff.id}>{tariff.name}</option>)}</SelectInput>}</Field>
        <Field label="Состояние">{(id) => <SelectInput id={id} value={chosenState} onChange={(event) => setState(event.target.value)}>{SERVICE_STATES.map((item) => <option key={item}>{item}</option>)}</SelectInput>}</Field>
        <Field label="Причина">{(id) => <TextInput id={id} placeholder="Например, заявление абонента" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
        {error && <Notice tone="warn">{error}</Notice>}
        <h3 className="section-title" style={{ marginTop: 6 }}>Сменить услугу</h3>
        {targets.length > 0
          ? (
            <>
              <p className="small muted" style={{ lineHeight: 1.45 }}>«{line?.name}» закроется с рабочего месяца, долг {formatMoney(line?.debt ?? 0)} ₽ перейдёт на выбранную услугу. Причина берётся из поля выше.</p>
              <Field label="Перейти на">{(id) => <SelectInput id={id} value={target?.id ?? ''} onChange={(event) => setReplaceWith(event.target.value)}>{targets.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</SelectInput>}</Field>
              <Button disabled={!reason.trim() || !target} onClick={() => void replace()}>Сменить услугу</Button>
            </>
          )
          : <p className="small muted" style={{ lineHeight: 1.45 }}>Другой действующей услуги нет. Сначала добавьте услугу, на которую переходит абонент.</p>}
        <h3 className="section-title" style={{ marginTop: 6 }}>История</h3>
        {(terms ?? []).map((term) => (
          <div key={term.fromPeriod} className="small" style={{ borderBottom: '1px solid var(--line-soft)', paddingBottom: 6 }}>
            <div className="strong">{formatPeriod(term.fromPeriod)}</div>
            <div>{term.tariffName}, {term.state.toLowerCase()}</div>
            <div className="muted">{term.who}, {term.when}{term.reason ? `. ${term.reason}` : ''}</div>
          </div>
        ))}
      </div>
      <div className="pad" style={{ borderTop: '1px solid var(--line)' }}>
        <Button variant="primary" size="lg" disabled={!serviceId || !month || !reason.trim()} onClick={() => void submit()}>Сохранить условия</Button>
      </div>
    </aside>
  );
}

interface DrawerProps {
  abonent: Abonent;
  kind: Exclude<Drawer, 'terms'>;
  onClose: () => void;
  onDone: (text: string) => void;
}

function ActionDrawer({ abonent, kind, onClose, onDone }: DrawerProps) {
  const { data: month } = useApi(() => api.period.current(), []);
  const { data: documents } = useApi(() => (kind === 'docs' ? api.documents.forAbonent(abonent.id) : Promise.resolve(null)), [kind, abonent.id]);
  const navigate = useNavigate();
  const monthName = month?.title.split(' ')[0]?.toLowerCase() ?? 'месяц';
  const [service, setService] = useState('Горячая вода');
  const [address, setAddress] = useState<AddressValue>(() => addressOf(abonent));
  const [error, setError] = useState<string | null>(null);
  const titles: Record<DrawerProps['kind'], string> = { charge: 'Начислить абоненту', service: 'Добавить услугу', address: 'Сменить адрес', docs: 'Документы абонента' };
  const submit = async () => {
    setError(null);
    try {
      if (kind === 'charge') {
        const result = await api.abonents.recharge(abonent.id);
        onDone(`Начислено ${formatMoney(result.total)} ₽ за ${monthName}.${result.issues.length ? ` Не начислено: ${result.issues.map((issue) => `${issue.service} (${issue.problem.toLowerCase()})`).join(', ')}.` : ''}`);
      }
      if (kind === 'service') { await api.abonents.addService(abonent.id, service); onDone(`Услуга «${service}» добавлена.`); }
      if (kind === 'docs') navigate(`/documents?abonent=${encodeURIComponent(abonent.id)}`);
      if (kind === 'address') { await api.abonents.changeAddress(abonent.id, { ...address, flat: address.flat.trim() }); onDone(`Адрес изменён: ${formatAddress(address)}.`); }
    } catch (reason) {
      setError(errorText(reason));
    }
  };
  const disabled = kind === 'address' && !isAddressComplete(address);
  const buttonLabel: Record<DrawerProps['kind'], string> = { charge: `Пересчитать ${monthName}`, service: 'Добавить услугу', address: 'Сменить адрес', docs: 'Все документы' };
  const waterLine = abonent.services.find((line) => line.meter);

  return (
    <aside className="pane pane-right" aria-label={titles[kind]} style={{ width: 340, background: 'var(--surface)' }}>
      <div className="bar" style={{ justifyContent: 'space-between' }}>
        <h2 style={{ fontSize: 17, fontWeight: 600 }}>{titles[kind]}</h2>
        <Button size="sm" onClick={onClose}>Закрыть</Button>
      </div>
      <div className="scroll stack pad">
        {kind === 'charge' && (
          <>
            <p className="small muted" style={{ lineHeight: 1.5 }}>Пересчёт заменит начисления этого абонента за этот месяц. Сумма не удвоится, сколько бы раз вы его ни запускали.</p>
            <Field label="Абонент">{(id) => <TextInput id={id} value={abonent.shortName} readOnly />}</Field>
            <Field label="Период">{(id) => <TextInput id={id} value={month?.title ?? ''} readOnly />}</Field>
            {waterLine?.meter && <p className="small muted">Последнее показание: {waterLine.meter.lastReading}. Начисление по нему: {formatMoney(chargeForReading(waterLine, waterLine.meter.lastReading))} ₽.</p>}
          </>
        )}
        {kind === 'service' && (
          <>
            <p className="small muted" style={{ lineHeight: 1.5 }}>Услуга и тариф. Если услуга по счётчику, система сразу попросит его номер.</p>
            <Field label="Услуга">{(id) => <TextInput id={id} value={service} onChange={(event) => setService(event.target.value)} />}</Field>
          </>
        )}
        {kind === 'address' && (
          <>
            <p className="small muted" style={{ lineHeight: 1.5 }}>{abonent.kind === 'org' ? 'Изменятся адреса абонента, всех его заказов и организации.' : 'Изменятся адреса абонента и всех его заказов.'}</p>
            <Field label="Сейчас">{(id) => <TextInput id={id} value={fullAddress(abonent)} readOnly />}</Field>
            <h3 className="field-label" style={{ marginTop: 4 }}>Новый адрес</h3>
            <AddressPicker value={address} onChange={setAddress} />
          </>
        )}
        {kind === 'docs' && (
          <>
            {(documents?.documents ?? []).length === 0 && <p className="small muted" style={{ lineHeight: 1.5 }}>Документов пока нет. Они формируются после закрытия месяца.</p>}
            {documents?.documents.map((item) => (
              <div key={item.id} className="between small" style={{ borderBottom: '1px solid var(--line-soft)', paddingBottom: 6 }}>
                <span>{item.kindName} № {item.number}, {item.periodTitle.toLowerCase()}{item.stale ? '. Нужно перевыпустить' : ''}</span>
                <a target="_blank" rel="noreferrer" href={`/print/documents?ids=${item.id}`}>Открыть</a>
              </div>
            ))}
          </>
        )}
        {error && <Notice tone="warn">{error}</Notice>}
      </div>
      <div className="pad" style={{ borderTop: '1px solid var(--line)' }}>
        <Button variant="primary" size="lg" disabled={disabled} onClick={() => void submit()}>{buttonLabel[kind]}</Button>
      </div>
    </aside>
  );
}
