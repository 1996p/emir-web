/** Все суммы в копейках: целые числа не дают ошибок округления. */
export type Kopecks = number;

export type Role = 'cashier' | 'service' | 'accountant' | 'admin';
export type AbonentKind = 'person' | 'org';
export type ServiceId = 'heat' | 'water' | 'penalty';
export type ServiceState = 'Работает' | 'Приостановлена' | 'Расторгнута';

export interface Meter {
  number: string;
  lastReading: number;
  tariff: Kopecks;
  verifiedUntil: string;
  /** Единица показаний; null — в единицах тарифа */
  unit?: string | null;
}

export interface ServiceLine {
  id: ServiceId;
  name: string;
  state: ServiceState;
  tariffName: string;
  price: number | null;
  chargeStatus: string;
  debt: Kopecks;
  meter?: Meter;
  houseMeterId?: string | null;
  tariffId?: string | null;
}

export interface Abonent {
  id: string;
  kind: AbonentKind;
  name: string;
  shortName: string;
  place: string;
  street: string;
  flat?: string;
  account: string;
  phone?: string;
  inn?: string;
  residents: number;
  area: number;
  credit: Kopecks;
  services: ServiceLine[];
}

export interface PaymentRecord {
  id: string;
  abonentId: string;
  date: string;
  source: string;
  cashier: string;
  amount: Kopecks;
  status: 'Проведена' | 'Отменена';
}

export interface JournalRecord {
  id: string;
  abonentId?: string;
  when: string;
  action: string;
  who: string;
  details: string;
  reason?: string;
}

export type PaymentSource = 'cash-person' | 'cash-org' | 'bank';

export interface PaymentAllocation {
  serviceId: ServiceId;
  amount: Kopecks;
}

export interface CreatePaymentRequest {
  abonentId: string;
  source: PaymentSource;
  allocations: PaymentAllocation[];
  reading?: number;
}

export interface CreatePaymentResult {
  paymentId: string;
  total: Kopecks;
  newBalance: Kopecks;
}

export interface ShiftEntry {
  id: string;
  time: string;
  name: string;
  amount: Kopecks;
}

export interface ShiftInfo {
  entries: ShiftEntry[];
  count: number;
  total: Kopecks;
}

export interface ManagedUser {
  id: number;
  login: string;
  displayName: string;
  role: Role;
  isActive: boolean;
  mustChangePassword: boolean;
  locked: boolean;
  lastLogin: string | null;
  isSelf: boolean;
}

export interface CurrentUser {
  login: string;
  displayName: string;
  role: Role;
  mustChangePassword: boolean;
}

export interface PeriodSetting {
  title: string;
  details: string;
}

export interface ChargeIssue {
  abonent: string;
  service: string;
  problem: string;
}

export interface ServicePreview {
  service: string;
  abonents: number;
  previous: Kopecks;
  already: Kopecks;
  current: Kopecks;
}

export interface ChargePreview {
  abonents: number;
  lines: number;
  total: Kopecks;
  already: Kopecks;
  services: ServicePreview[];
  issues: ChargeIssue[];
}

export interface ChargeRunResult {
  abonents: number;
  lines: number;
  total: Kopecks;
  issues: ChargeIssue[];
}

export interface TariffPriceItem {
  fromPeriod: string;
  price: number;
  who: string;
  when: string;
  reason: string | null;
}

export interface TariffItem {
  id: string;
  name: string;
  scheme: string;
  currentPrice: number | null;
  lines: number;
  prices: TariffPriceItem[];
  unit?: string | null;
  serviceNorm: string | null;
  tariffNorm: string | null;
  penaltyEnabled: boolean;
}

export interface TariffsView {
  openPeriod: string;
  reopened: boolean;
  tariffs: TariffItem[];
}

export interface HeatNormRow {
  category: string;
  abonents: number;
  withoutNorm: number;
  norm: number | null;
  distinct: number[];
}

export interface HeatNormsView {
  categories: HeatNormRow[];
  withoutCategory: number;
}

export interface ServiceTermItem {
  fromPeriod: string;
  tariffId: string | null;
  tariffName: string;
  state: string;
  who: string;
  when: string;
  reason: string | null;
}

export interface ChargeEntry {
  period: string;
  service: string;
  volume: number;
  scheme: string;
  price: number | null;
  amount: Kopecks;
}

export interface ReadingRow {
  id: string;
  house: string;
  flat: string;
  meterNumber: string;
  service: string;
  previous: number;
  average: number;
  value: string;
}

export interface HouseProgress {
  id: string;
  name: string;
  done: number;
  total: number;
}

export interface CurrentMonth {
  period: string;
  title: string;
  status: 'open' | 'closed' | 'reopened';
  nextTitle: string | null;
  reopenReason: string | null;
}

export interface ClosedMonth {
  period: string;
  month: string;
  closedAt: string;
  closedBy: string | null;
  reclosedCount: number;
  reclosedAt: string | null;
  reopened: boolean;
  cannotReopen: string | null;
}

export interface RecloseChange {
  abonentId: string;
  abonent: string;
  serviceId: string;
  service: string;
  oldAmount: Kopecks;
  newAmount: Kopecks;
  oldPrice: number | null;
  newPrice: number | null;
  delta: Kopecks;
}

export interface ReclosePreview {
  period: string;
  title: string;
  delta: Kopecks;
  changes: RecloseChange[];
  issues: ChargeIssue[];
  blocking: ChargeIssue[];
}

export interface CheckRow {
  abonentId: string | null;
  cells: string[];
}

export interface CheckGroup {
  id: string;
  title: string;
  description: string;
  severity: 'error' | 'warn' | 'info';
  columns: string[];
  rows: CheckRow[];
  action: string | null;
  actionTo: string | null;
}

export interface ChecksView {
  period: string;
  title: string;
  months: Array<{ period: string; title: string }>;
  groups: CheckGroup[];
}

export interface DocumentLine {
  service: string;
  unit: string;
  unitCode: string;
  volume: number | null;
  price: number | null;
  amount: Kopecks;
  vat: Kopecks;
  debt: Kopecks;
}

export interface DocumentData {
  title: string;
  number: string;
  revision: number;
  date: string;
  revisionDate: string;
  periodTitle: string;
  contract: string;
  seller: { name: string; inn: string; kpp: string | null; address: string; bankName: string | null; bik: string | null; corrAccount: string | null; account: string | null; director: string | null; chiefAccountant: string | null };
  buyer: { name: string; inn: string | null; kpp: string | null; address: string };
  vatRate: number | null;
  lines: DocumentLine[];
  total: Kopecks;
  vatTotal: Kopecks;
  debt: Kopecks;
  credit: Kopecks;
  toPay: Kopecks;
  totalWords: string;
  toPayWords: string;
  actNumber: string | null;
}

export interface PrintDocument {
  id: number;
  kind: string;
  status: string;
  data: DocumentData;
}

export interface DocumentRow {
  id: number;
  kind: string;
  kindName: string;
  number: string;
  revision: number;
  period: string;
  periodTitle: string;
  date: string;
  abonentId: string;
  abonent: string;
  total: Kopecks;
  status: string;
  stale: boolean;
  reason: string | null;
}

export interface DocumentsView {
  period: string;
  title: string;
  months: Array<{ period: string; title: string }>;
  canGenerate: boolean;
  cannotGenerate: string | null;
  documents: DocumentRow[];
}

export interface OrganizationProfile {
  name: string;
  inn: string;
  kpp: string | null;
  address: string;
  bankName: string | null;
  bik: string | null;
  corrAccount: string | null;
  account: string | null;
  director: string | null;
  chiefAccountant: string | null;
  vatRate: number | null;
  heatSeasonStartMonth: number;
  heatSeasonEndMonth: number;
}

export interface DocumentCounter {
  year: number;
  kind: string;
  kindName: string;
  last: number;
}

export interface LedgerRow {
  id: number;
  when: string;
  kind: string;
  kindName: string;
  period: string | null;
  service: string;
  debtDelta: Kopecks;
  creditDelta: Kopecks;
  balanceAfter: Kopecks;
  who: string;
  reference: string | null;
}

export interface AbonentDetails {
  note: string | null;
  consent: boolean | null;
  consentWhen: string | null;
  ownershipDocument: string | null;
  phone: string | null;
  openPeriod: string;
  reopened: boolean;
  facts: Array<{ fromPeriod: string; area: number; residents: number; who: string; when: string; reason: string | null }>;
  residents: Array<{ id: number; name: string; fromDate: string | null; toDate: string | null }>;
  /** Норма теплопотребления дома, Гкал/м² */
  houseHeatNorm: number | null;
}

export interface BenefitCategory {
  id: number;
  name: string;
  isActive: boolean;
  beneficiaries: number;
}

export interface Beneficiary {
  id: number;
  abonentId: string;
  abonent: string;
  account: string;
  address: string;
  categoryId: number;
  category: string;
  fullName: string;
  fromDate: string | null;
  toDate: string | null;
  active: boolean;
  debt: Kopecks;
  debtor: boolean;
  uuid: string;
}

export interface BenefitImportLine {
  line: number;
  account: string;
  fullName: string;
  category: string;
  fromDate: string | null;
  state: string;
}

export interface CancelLine {
  abonentId: string;
  abonent: string;
  account: string;
  serviceId: string;
  service: string;
  amount: Kopecks;
}

export interface CancelPreview {
  period: string;
  title: string;
  total: Kopecks;
  lines: CancelLine[];
  services: Array<{ id: string; name: string; total: Kopecks }>;
  places: string[];
  chargesDone: boolean;
  penaltyDone: boolean;
}

export interface ReadingImportLine {
  line: number;
  account: string;
  meter: string;
  value: string;
  previous: number | null;
  usage: number | null;
  charge: Kopecks;
  state: string;
}

export interface ReadingImportResult {
  applied: boolean;
  title: string;
  applicable: number;
  high: number;
  charge: Kopecks;
  saved: number;
  lines: ReadingImportLine[];
}

export type StepState = 'done' | 'current' | 'next';

export interface PeriodIssue {
  title: string;
  details: string;
  action: string;
  to: string;
  warn?: boolean;
}

export interface PeriodStat {
  label: string;
  value: string;
  warn?: boolean;
}

export interface PeriodStep {
  id: string;
  name: string;
  status: string;
  state: StepState;
  description: string;
  stats: PeriodStat[];
  issues: PeriodIssue[];
  action: string;
  confirmTitle: string | null;
  confirmText: string | null;
  confirmAction: string | null;
}

export type ReportKind = 'debt' | 'cash' | 'sum' | 'use' | 'bank';

export interface ReportDefinition {
  id: string;
  name: string;
  group: string;
  kind: ReportKind;
  cuts: string[];
}

export interface ReportQuery {
  reportId: string;
  period: string;
  cut: string;
  who: string;
}

export interface ReportColumn {
  title: string;
  /** count: целое, money: копейки, percent: десятые доли процента, volume: тысячные доли (Гкал, м³) */
  type: 'count' | 'money' | 'percent' | 'volume' | 'norm';
  noTotal?: boolean;
}

export interface ReportRow {
  name: string;
  values: number[];
}

export interface ReportResult {
  available: boolean;
  message: string | null;
  columns: ReportColumn[];
  rows: ReportRow[];
  totals: Array<number | null>;
}

export type CorrectionGroup = 'Платежи' | 'Начисления' | 'Баланс';

export interface CorrectionType {
  id: string;
  name: string;
  group: CorrectionGroup;
  description: string;
  amountLabel: string;
  /** Как операция меняет баланс: -1 снимает с абонента, 1 возвращает, 0 не меняет */
  balanceEffect: -1 | 0 | 1;
  defaultAmount: Kopecks;
  flow: string[];
  needsPayment: boolean;
  actionVerb: string;
}

export type ExchangeLineState = 'ok' | 'not-found' | 'duplicate';

export interface ExchangeLine {
  id: string;
  line: number;
  key: string;
  amount: Kopecks;
  state: ExchangeLineState;
}

export interface ExchangeBatch {
  id: string;
  partner: string;
  when: string;
  title: string;
  fileName: string;
  keyColumn: string;
  total: number;
  sum: Kopecks | null;
  posted: boolean;
  lines: ExchangeLine[];
}

export interface DirectoryGroup {
  name: string;
  items: string[];
}

export interface DirectoryTable {
  columns: string[];
  rows: string[][];
  sensitive?: boolean;
  placeholder?: boolean;
  /** Строки можно добавлять, менять и удалять */
  editable?: boolean;
}

export interface AdminSection {
  id: string;
  group: string;
  title: string;
  description: string;
  columns: string[];
  rows: string[][];
  badge?: number;
}

export interface TodayTask {
  title: string;
  details: string;
  value: string;
  warn?: boolean;
  to: string;
}

export interface TodayMessage {
  title: string;
  details: string;
}

export interface TodayView {
  tasks: TodayTask[];
  actions: Array<{ label: string; to: string }>;
  messages: TodayMessage[];
}

export interface RegisterAbonentRequest {
  kind: AbonentKind;
  name: string;
  address: string;
  phone?: string;
  inn?: string;
}
