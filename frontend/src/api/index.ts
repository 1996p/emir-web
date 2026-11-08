import { useCallback, useEffect, useRef, useState, type DependencyList } from 'react';
import type {
  Abonent, AdminSection, ChargeEntry, ChargePreview, ChargeRunResult, CorrectionType, CreatePaymentRequest, CreatePaymentResult, CurrentUser, DirectoryGroup, DirectoryTable, ExchangeBatch, HouseProgress,
  JournalRecord, Kopecks, PaymentRecord, PeriodSetting, PeriodStep, ReadingRow, RegisterAbonentRequest, ReportDefinition, ReportQuery, ReportResult, ShiftInfo, TodayView,
} from '@/domain/types';
import type { CancelPreview, ReadingImportResult } from '@/domain/types';
import type { Beneficiary, BenefitCategory, BenefitImportLine } from '@/domain/types';
import type { AbonentDetails, LedgerRow, ManagedUser, Role } from '@/domain/types';
import type { DocumentCounter, DocumentRow, DocumentsView, OrganizationProfile, PrintDocument } from '@/domain/types';
import type { ChecksView, ClosedMonth, CurrentMonth, ReclosePreview, ServiceTermItem, TariffsView } from '@/domain/types';
import type { AddressValue, HeatNormsView, MonthReadiness } from '@/domain/types';

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

let onUnauthorized: () => void = () => undefined;
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler;
}

// Сколько экранов ждут первых данных: индикатор нужен только тогда, когда показывать ещё нечего.
// Фоновые обновления уже открытого экрана его не включают, иначе он ложится поверх данных.
let pending = 0;
const loadingListeners = new Set<() => void>();
function setPending(delta: number) {
  pending += delta;
  loadingListeners.forEach((listener) => listener());
}
export function subscribeLoading(listener: () => void) {
  loadingListeners.add(listener);
  return () => { loadingListeners.delete(listener); };
}
export const getPendingRequests = () => pending;

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api${path}`, { credentials: 'same-origin', ...init });
  } catch {
    throw new ApiError(0, 'Сервер недоступен. Проверьте соединение');
  }
  if (response.status === 401 && path !== '/auth/login') onUnauthorized();
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { detail?: string } | null;
    const fallback = response.status === 403 ? 'Недостаточно прав для этого действия' : response.status === 429 ? 'Слишком много попыток. Подождите минуту' : 'Не удалось выполнить запрос';
    throw new ApiError(response.status, body?.detail ?? fallback);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

const get = <T,>(path: string) => request<T>(path);

const listeners = new Set<() => void>();
function notify() {
  listeners.forEach((listener) => listener());
}

/** Выполняет изменение и обновляет данные на открытых экранах. */
async function mutate<T>(path: string, body?: unknown, headers?: Record<string, string>): Promise<T> {
  const init: RequestInit = { method: 'POST', headers: { ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...headers } };
  if (body !== undefined) init.body = JSON.stringify(body);
  const result = await request<T>(path, init);
  notify();
  return result;
}

const q = (params: Record<string, string>) => new URLSearchParams(params).toString();

export interface ReportParams extends ReportQuery {
  from?: string;
  to?: string;
}

function reportQuery(query: ReportParams): string {
  const params: Record<string, string> = { reportId: query.reportId, period: query.period, cut: query.cut, who: query.who };
  if (query.from) params.from = query.from;
  if (query.to) params.to = query.to;
  return q(params);
}

export const api = {
  auth: {
    me: () => get<CurrentUser>('/auth/me'),
    login: (login: string, password: string) => request<CurrentUser>('/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login, password }) }),
    logout: () => request<void>('/auth/logout', { method: 'POST' }),
    changePassword: (currentPassword: string, newPassword: string) => mutate<void>('/auth/password', { currentPassword, newPassword }),
  },
  abonents: {
    list: () => get<Abonent[]>('/abonents'),
    payments: (id: string) => get<PaymentRecord[]>(`/abonents/${encodeURIComponent(id)}/payments`),
    journal: (id: string) => get<JournalRecord[]>(`/abonents/${encodeURIComponent(id)}/journal`),
    register: (body: RegisterAbonentRequest) => mutate<Abonent>('/abonents', body),
    charges: (id: string) => get<ChargeEntry[]>(`/abonents/${encodeURIComponent(id)}/charges`),
    get: (id: string) => get<Abonent>(`/abonents/${encodeURIComponent(id)}`),
    details: (id: string) => get<AbonentDetails>(`/abonents/${encodeURIComponent(id)}/details`),
    setNote: (id: string, note: string | null) => mutate<void>(`/abonents/${encodeURIComponent(id)}/note`, { note }),
    setContact: (id: string, phone: string | null) => mutate<void>(`/abonents/${encodeURIComponent(id)}/contact`, { phone }),
    setLegal: (id: string, consent: boolean | null, ownershipDocument: string | null) => mutate<void>(`/abonents/${encodeURIComponent(id)}/legal`, { consent, ownershipDocument }),
    setHeatNorm: (id: string, norm: number | null, reason: string) => mutate<void>(`/abonents/${encodeURIComponent(id)}/heat-norm`, { norm, reason }),
    setHouseCategory: (id: string, category: string | null, reason: string) => mutate<void>(`/abonents/${encodeURIComponent(id)}/house-category`, { category, reason }),
    setMeterUnit: (id: string, serviceId: string, unit: string | null) => mutate<void>(`/abonents/${encodeURIComponent(id)}/services/${encodeURIComponent(serviceId)}/meter-unit`, { unit }),
    setMeter: (id: string, serviceId: string, body: { number: string; lastReading: number; verifiedUntil: string | null; average: number | null; unit: string | null }) => mutate<void>(`/abonents/${encodeURIComponent(id)}/services/${encodeURIComponent(serviceId)}/meter`, body),
    removeMeter: (id: string, serviceId: string) => mutate<void>(`/abonents/${encodeURIComponent(id)}/services/${encodeURIComponent(serviceId)}/meter/remove`),
    setHouseMeter: (id: string, serviceId: string, houseMeterId: string | null) => mutate<void>(`/abonents/${encodeURIComponent(id)}/services/${encodeURIComponent(serviceId)}/house-meter`, { houseMeterId }),
    setFact: (id: string, body: { fromPeriod: string; area: number; residents: number; reason: string }) => mutate<void>(`/abonents/${encodeURIComponent(id)}/facts`, body),
    addResident: (id: string, name: string, fromDate: string | null) => mutate<void>(`/abonents/${encodeURIComponent(id)}/residents`, { name, fromDate }),
    endResident: (id: string, residentId: number, toDate: string) => mutate<void>(`/abonents/${encodeURIComponent(id)}/residents/${residentId}/end`, { toDate }),
    ledger: (id: string) => get<LedgerRow[]>(`/abonents/${encodeURIComponent(id)}/ledger`),
    recharge: (id: string) => mutate<ChargeRunResult>(`/abonents/${encodeURIComponent(id)}/recharge`),
    changeAddress: (id: string, address: AddressValue) => mutate<Abonent>(`/abonents/${encodeURIComponent(id)}/address`, address),
    addService: (id: string, serviceName: string) => mutate<Abonent>(`/abonents/${encodeURIComponent(id)}/services`, { serviceName }),
    terms: (id: string, serviceId: string) => get<ServiceTermItem[]>(`/abonents/${encodeURIComponent(id)}/services/${encodeURIComponent(serviceId)}/terms`),
    setTerm: (id: string, serviceId: string, body: { fromPeriod: string; tariffId: string | null; state: string; reason: string }) => mutate<void>(`/abonents/${encodeURIComponent(id)}/services/${encodeURIComponent(serviceId)}/terms`, body),
  },
  cash: {
    /** Один ключ на одну попытку оплаты: повторная отправка не создаст второй платёж. */
    pay: (body: CreatePaymentRequest, idempotencyKey: string) => mutate<CreatePaymentResult>('/cash/payments', body, { 'Idempotency-Key': idempotencyKey }),
    refund: (abonentId: string, paymentId: string, reason: string) => mutate<{ amount: Kopecks; newBalance: Kopecks }>('/cash/refunds', { abonentId, paymentId, reason }),
    shift: () => get<ShiftInfo>('/cash/shift'),
  },
  readings: {
    houseMeters: () => get<HouseMeters>('/readings/house-meters'),
    createHouseMeter: (body: HouseMeterBody) => mutate<{ id: string }>('/readings/house-meters/new', body),
    editHouseMeter: (id: string, body: HouseMeterBody) => mutate<void>(`/readings/house-meters/${encodeURIComponent(id)}/edit`, body),
    saveHouseMeter: (id: string, value: number) => mutate<void>(`/readings/house-meters/${encodeURIComponent(id)}`, { value }),
    linkCandidates: (id: string) => get<HouseMeterCandidates>(`/readings/house-meters/${encodeURIComponent(id)}/candidates`),
    linkLines: (id: string, keys: string[], link: boolean, reason: string) => mutate<{ changed: number }>(`/readings/house-meters/${encodeURIComponent(id)}/link`, { keys, link, reason }),
    importFile: async (file: File, columns: Record<string, string>, options: { apply?: boolean; includeHigh?: boolean; expected?: number }) => {
      const form = new FormData();
      form.append('file', file);
      const params: Record<string, string> = { ...columns };
      if (options.apply) params.apply = 'true';
      if (options.includeHigh) params.includeHigh = 'true';
      if (options.expected !== undefined) params.expected = String(options.expected);
      const result = await request<ReadingImportResult>(`/readings/import?${q(params)}`, { method: 'POST', body: form });
      notify();
      return result;
    },
    houses: () => get<HouseProgress[]>('/readings/houses'),
    rows: (house: string) => get<ReadingRow[]>(`/readings/rows?${q({ house })}`),
    /** Экран не перезагружается после сохранения, чтобы не сбить ввод в других строках. */
    save: (rowId: string, value: number) => {
      const [abonentId, serviceId] = rowId.split(':');
      return request<void>('/readings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ abonentId, serviceId, value }) });
    },
  },
  period: {
    steps: () => get<PeriodStep[]>('/period/steps'),
    preview: () => get<ChargePreview>('/period/charges/preview'),
    readiness: () => get<MonthReadiness>('/period/readiness'),
    run: (stepId: string) => mutate<{ message: string }>(`/period/steps/${encodeURIComponent(stepId)}/run`),
    settings: () => get<PeriodSetting[]>('/period/settings'),
    closed: () => get<ClosedMonth[]>('/period/closed'),
    current: () => get<CurrentMonth>('/period/current'),
    openNext: () => mutate<{ message: string }>('/period/open-next'),
    reopen: (period: string, reason: string) => mutate<{ message: string }>(`/period/months/${encodeURIComponent(period)}/reopen`, { reason }),
    cancelPreview: (filter: { kind: string; place: string; query: string; services: string[] }) => get<CancelPreview>(`/period/cancel/preview?${q({ kind: filter.kind, place: filter.place, query: filter.query, services: filter.services.join(',') })}`),
    cancel: (body: { kind: string; place: string; query: string; services: string[]; reason: string; expectedTotal: Kopecks; expectedLines: number }) => mutate<{ message: string }>('/period/cancel', body),
    reclosePreview: () => get<ReclosePreview>('/period/reclose/preview'),
    reclose: (reason: string, expectedDelta: Kopecks) => mutate<{ message: string }>('/period/reclose', { reason, expectedDelta }),
  },
  corrections: {
    types: () => get<CorrectionType[]>('/corrections/types'),
    /** operationDate — дата документа-основания, ГГГГ-ММ-ДД; деньги проводятся в открытом месяце */
    apply: (abonentId: string, typeId: string, amount: Kopecks, reason: string, paymentId?: string, operationDate?: string) => mutate<Abonent>('/corrections', { abonentId, typeId, amount, reason, paymentId: paymentId ?? null, operationDate: operationDate || null }),
  },
  reports: {
    catalog: () => get<ReportDefinition[]>('/reports/catalog'),
    run: (query: ReportParams) => get<ReportResult>(`/reports/run?${reportQuery(query)}`),
    csvUrl: (query: ReportParams) => `/api/reports/export.csv?${reportQuery(query)}`,
  },
  exchange: {
    batches: () => get<ExchangeBatch[]>('/exchange/batches'),
    operations: (partner: string) => get<string[]>(`/exchange/operations?${q({ partner })}`),
    post: (batchId: string) => mutate<ExchangeBatch>(`/exchange/batches/${encodeURIComponent(batchId)}/post`),
    upload: async (file: File) => {
      const form = new FormData();
      form.append('file', file);
      const result = await request<ExchangeBatch>('/exchange/upload', { method: 'POST', body: form });
      notify();
      return result;
    },
  },
  benefits: {
    categories: () => get<BenefitCategory[]>('/benefits/categories'),
    addCategory: (name: string) => mutate<void>('/benefits/categories', { name }),
    saveCategory: (id: number, name: string, isActive: boolean) => mutate<void>(`/benefits/categories/${id}`, { name, isActive }),
    list: () => get<Beneficiary[]>('/benefits'),
    forAbonent: (abonentId: string) => get<Beneficiary[]>(`/benefits?${q({ abonentId })}`),
    add: (body: { abonentId: string; categoryId: number; fullName: string; fromDate: string | null }) => mutate<void>('/benefits', body),
    end: (id: number, toDate: string, reason: string) => mutate<void>(`/benefits/${id}/end`, { toDate, reason }),
    responseUrl: (period: string) => `/api/benefits/response.csv?${q({ period })}`,
    importFile: async (file: File, apply: boolean) => {
      const form = new FormData();
      form.append('file', file);
      const result = await request<{ applied: boolean; added: number; lines: BenefitImportLine[] }>(`/benefits/import?${q({ apply: String(apply) })}`, { method: 'POST', body: form });
      notify();
      return result;
    },
  },
  documents: {
    list: (period?: string, kind?: string) => { const params: Record<string, string> = {}; if (period) params.period = period; if (kind) params.kind = kind; return get<DocumentsView>(`/documents?${q(params)}`); },
    forAbonent: (abonentId: string) => get<DocumentsView>(`/documents?${q({ abonentId })}`),
    history: async (abonentId: string, kind: string): Promise<DocumentRow[]> => (await get<DocumentsView>(`/documents?${q({ abonentId, kind, history: 'true' })}`)).documents,
    print: (ids: string) => get<PrintDocument[]>(`/documents/print?${q({ ids })}`),
    generate: (period: string) => mutate<{ created: number; existing: number; abonents: number }>('/documents/generate', { period }),
    reissue: (id: number, reason: string) => mutate<{ id: number }>(`/documents/${id}/reissue`, { reason }),
    reissueStale: (period: string, reason: string) => mutate<{ reissued: number }>('/documents/reissue-stale', { period, reason }),
    profile: () => get<OrganizationProfile>('/documents/profile'),
    saveProfile: (body: OrganizationProfile) => mutate<void>('/documents/profile', body),
    counters: () => get<DocumentCounter[]>('/documents/counters'),
    setCounter: (body: { year: number; kind: string; last: number }) => mutate<void>('/documents/counters', body),
  },
  checks: {
    get: (period?: string) => get<ChecksView>(`/checks${period ? `?${q({ period })}` : ''}`),
    csvUrl: (id: string, period: string) => `/api/checks/${encodeURIComponent(id)}/export.csv?${q({ period })}`,
  },
  tariffs: {
    list: () => get<TariffsView>('/tariffs'),
    create: (body: SaveTariffBody) => mutate<{ id: string }>('/tariffs', body),
    update: (id: string, body: SaveTariffBody) => mutate<void>(`/tariffs/${encodeURIComponent(id)}`, body),
    setPrice: (id: string, body: { fromPeriod: string; price: number; reason: string }) => mutate<void>(`/tariffs/${encodeURIComponent(id)}/prices`, body),
    cancelPrice: (id: string, fromPeriod: string, reason: string) => mutate<void>(`/tariffs/${encodeURIComponent(id)}/prices/${encodeURIComponent(fromPeriod)}/cancel`, { reason }),
  },
  heatNorms: {
    list: () => get<HeatNormsView>('/heat-norms'),
    apply: (body: { category: string; norm: number; reason: string }) => mutate<{ category: string; norm: number; total: number; changed: number }>('/heat-norms/apply', body),
  },
  /** Адрес каскадом: населённый пункт → улица → дом. Новые улицы и дома заводятся прямо из формы адреса. */
  addresses: {
    cities: () => get<string[]>('/addresses/cities'),
    streets: (place: string) => get<string[]>(`/addresses/streets?${q({ place })}`),
    houses: (place: string, street: string) => get<string[]>(`/addresses/houses?${q({ place, street })}`),
    addStreet: (place: string, street: string, reason: string) => mutate<void>('/addresses/streets', { place, street, reason }),
    addHouse: (place: string, street: string, house: string, reason: string) => mutate<void>('/addresses/houses', { place, street, house, reason }),
  },
  directories: {
    groups: () => get<DirectoryGroup[]>('/directories/groups'),
    table: (name: string) => get<DirectoryTable>(`/directories/table?${q({ name })}`),
    options: (name: string, column: string, where?: Record<string, string>) =>
      get<string[]>(`/directories/options?${q(where && Object.keys(where).length > 0 ? { name, column, where: JSON.stringify(where) } : { name, column })}`),
    addRow: (name: string, row: string[], reason: string) => mutate<void>('/directories/rows', { name, row, reason }),
    editRow: (name: string, index: number, expected: string[], row: string[], reason: string) => mutate<void>(`/directories/rows/${index}`, { name, expected, row, reason }),
    deleteRow: (name: string, index: number, expected: string[], reason: string) => mutate<void>(`/directories/rows/${index}/delete`, { name, expected, reason }),
    viewPassport: (account: string) => mutate<{ value: string }>(`/directories/people/${encodeURIComponent(account)}/passport`),
  },
  admin: {
    sections: () => get<AdminSection[]>('/admin/sections'),
    users: () => get<ManagedUser[]>('/admin/users'),
    createUser: (body: { login: string; displayName: string; role: Role }) => mutate<{ login: string; temporaryPassword: string }>('/admin/users', body),
    updateUser: (id: number, body: { displayName: string; role: Role }) => mutate<void>(`/admin/users/${id}`, body),
    setActive: (id: number, active: boolean) => mutate<void>(`/admin/users/${id}/active`, { active }),
    resetPassword: (id: number) => mutate<{ login: string; temporaryPassword: string }>(`/admin/users/${id}/reset-password`),
    unlock: (id: number) => mutate<void>(`/admin/users/${id}/unlock`),
  },
  today: {
    get: () => get<TodayView>('/today'),
  },
};

export function errorText(reason: unknown): string {
  return reason instanceof Error ? reason.message : 'Не удалось выполнить запрос';
}

export interface ApiState<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
}

/** Загружает данные и перезагружает их после изменений через api. */
export function useApi<T>(load: () => Promise<T>, deps: DependencyList): ApiState<T> {
  const [state, setState] = useState<ApiState<T>>({ data: null, error: null, loading: true });
  const hasData = useRef(false);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const run = useCallback(load, deps);

  useEffect(() => {
    let alive = true;
    const fetchData = () => {
      let first = !hasData.current;
      if (first) setPending(1);
      const done = () => { if (first) { first = false; setPending(-1); } };
      run().then(
        (data) => { done(); hasData.current = true; if (alive) setState({ data, error: null, loading: false }); },
        (error: unknown) => { done(); if (alive) setState((current) => ({ data: current.data, error: errorText(error), loading: false })); },
      );
    };
    fetchData();
    listeners.add(fetchData);
    return () => {
      alive = false;
      listeners.delete(fetchData);
    };
  }, [run]);

  return state;
}

export interface HouseMeterRow {
  id: string;
  place: string;
  street: string;
  number: string;
  unit: string;
  unitFactor: number;
  previous: number | null;
  value: number | null;
  lines: number;
}

export interface HouseMeters {
  period: string;
  rows: HouseMeterRow[];
}

export interface LinkCandidate {
  key: string;
  abonent: string;
  flat: string;
  service: string;
  linkedToId: string | null;
  linkedToName: string | null;
}

export interface HouseMeterCandidates {
  meter: string;
  chargedThisMonth: boolean;
  candidates: LinkCandidate[];
}

export interface SaveTariffBody {
  name: string;
  scheme: string;
  unit: string | null;
  serviceNorm: string | null;
  tariffNorm: string | null;
  penaltyEnabled: boolean;
  reason: string;
  price?: number | null;
  fromPeriod?: string | null;
}

export interface HouseMeterBody { place: string; street: string; number: string; unit: string }

/** Единицы показаний тепловых счётчиков. Основная — Гкал, остальные переводятся в Гкал. */
export const METER_UNITS: Array<{ value: string; label: string }> = [
  { value: 'Гкал', label: 'Гкал' },
  { value: 'ГДж', label: 'ГДж (1 Гкал = 4,186 ГДж)' },
  { value: 'МВт·ч', label: 'МВт·ч (1 Гкал = 1,163 МВт·ч)' },
  { value: 'кВт·ч', label: 'кВт·ч (1 Гкал = 1163 кВт·ч)' },
];
