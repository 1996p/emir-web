import { useId, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react';
import { Link } from 'react-router-dom';

function cx(...parts: Array<string | false | undefined>): string {
  return parts.filter(Boolean).join(' ');
}

export type ButtonVariant = 'default' | 'primary' | 'danger';
/** md, sm и lg имеют фиксированную ширину, чтобы кнопки в ряду совпадали. */
export type ButtonSize = 'md' | 'sm' | 'lg' | 'full' | 'auto';

function buttonClass(variant: ButtonVariant, size: ButtonSize, active?: boolean, extra?: string): string {
  return cx(
    'btn',
    variant !== 'default' && `btn-${variant}`,
    size !== 'auto' && `btn-${size}`,
    active && 'btn-active',
    extra,
  );
}

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  active?: boolean;
}

export function Button({ variant = 'default', size = 'md', active, className, type = 'button', ...rest }: ButtonProps) {
  return <button type={type} className={buttonClass(variant, size, active, className)} {...rest} />;
}

interface LinkButtonProps {
  to: string;
  variant?: ButtonVariant;
  size?: ButtonSize;
  children: ReactNode;
}

export function LinkButton({ to, variant = 'default', size = 'md', children }: LinkButtonProps) {
  return (
    <Link to={to} className={buttonClass(variant, size)}>
      {children}
    </Link>
  );
}

export interface Option<T extends string> {
  value: T;
  label: string;
}

interface SegmentedProps<T extends string> {
  options: ReadonlyArray<Option<T>>;
  value: T;
  onChange: (value: T) => void;
  label: string;
  itemWidth?: number;
}

export function Segmented<T extends string>({ options, value, onChange, label, itemWidth = 104 }: SegmentedProps<T>) {
  return (
    <div className="segmented" role="group" aria-label={label}>
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          style={{ width: itemWidth }}
          aria-pressed={option.value === value}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

interface FieldProps {
  label: string;
  hint?: ReactNode;
  children: (id: string) => ReactNode;
}

export function Field({ label, hint, children }: FieldProps) {
  const id = useId();
  return (
    <div>
      <label className="field-label" htmlFor={id}>
        {label}
      </label>
      {children(id)}
      {hint && <div className="field-hint muted">{hint}</div>}
    </div>
  );
}

interface TextInputProps extends InputHTMLAttributes<HTMLInputElement> {
  mono?: boolean;
  primary?: boolean;
  alignRight?: boolean;
  edited?: boolean;
}

export function TextInput({ mono, primary, alignRight, edited, className, ...rest }: TextInputProps) {
  return (
    <input
      className={cx('input', mono && 'input-mono', primary && 'input-primary', alignRight && 'input-right', edited && 'input-edited', className)}
      {...rest}
    />
  );
}

export function SelectInput({ className, ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select className={cx('select', className)} {...rest} />;
}

export function TextArea({ className, ...rest }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className={cx('textarea', className)} {...rest} />;
}

export interface TabItem<T extends string> {
  value: T;
  label: string;
}

interface TabsProps<T extends string> {
  items: ReadonlyArray<TabItem<T>>;
  value: T;
  onChange: (value: T) => void;
  label: string;
}

export function Tabs<T extends string>({ items, value, onChange, label }: TabsProps<T>) {
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {items.map((item) => (
        <button key={item.value} type="button" role="tab" aria-selected={item.value === value} onClick={() => onChange(item.value)}>
          {item.label}
        </button>
      ))}
    </div>
  );
}

export type NoticeTone = 'info' | 'ok' | 'warn';

export function Notice({ tone = 'info', children }: { tone?: NoticeTone; children: ReactNode }) {
  return (
    <div className={`notice notice-${tone}`} role={tone === 'warn' ? 'alert' : 'status'}>
      {children}
    </div>
  );
}

export interface Column<Row> {
  key: string;
  header: string;
  align?: 'left' | 'right';
  mono?: boolean;
  render: (row: Row) => ReactNode;
  footer?: ReactNode;
}

interface DataTableProps<Row> {
  columns: ReadonlyArray<Column<Row>>;
  rows: ReadonlyArray<Row>;
  rowKey: (row: Row) => string;
  minWidth?: number;
  onRowClick?: (row: Row) => void;
  isSelected?: (row: Row) => boolean;
  rowClass?: (row: Row) => string | undefined;
  empty?: ReactNode;
  footerLabel?: ReactNode;
}

export function DataTable<Row>({ columns, rows, rowKey, minWidth = 640, onRowClick, isSelected, rowClass, empty, footerLabel }: DataTableProps<Row>) {
  const hasFooter = columns.some((column) => column.footer !== undefined);
  return (
    <div className="scroll">
      <table className="table" style={{ minWidth }}>
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.key} className={column.align === 'right' ? 'right' : undefined}>
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr
              key={rowKey(row)}
              className={cx(onRowClick && 'is-clickable', isSelected?.(row) && 'is-selected', rowClass?.(row))}
              onClick={onRowClick ? () => onRowClick(row) : undefined}
            >
              {columns.map((column) => (
                <td key={column.key} className={cx(column.align === 'right' && 'right', column.mono && 'mono')}>
                  {column.render(row)}
                </td>
              ))}
            </tr>
          ))}
          {rows.length === 0 && empty && (
            <tr>
              <td colSpan={columns.length}>{empty}</td>
            </tr>
          )}
        </tbody>
        {hasFooter && (
          <tfoot>
            <tr>
              {columns.map((column, index) => (
                <td key={column.key} className={cx(column.align === 'right' && 'right', column.mono && 'mono')}>
                  {index === 0 && column.footer === undefined ? footerLabel : column.footer}
                </td>
              ))}
            </tr>
          </tfoot>
        )}
      </table>
    </div>
  );
}

export interface DefinitionItem {
  label: string;
  value: ReactNode;
  warn?: boolean;
}

export function DefinitionList({ items }: { items: ReadonlyArray<DefinitionItem> }) {
  return (
    <dl className="definition">
      {items.map((item) => (
        <div key={item.label}>
          <dt>{item.label}</dt>
          <dd className={item.warn ? 'warn' : undefined}>{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}

export interface AccordionGroup<T> {
  name: string;
  items: ReadonlyArray<T>;
}

interface AccordionNavProps<T> {
  groups: ReadonlyArray<AccordionGroup<T>>;
  /** При поиске раскрываем все группы */
  forceOpen?: boolean;
  openGroup: string | null;
  onOpenGroup: (group: string | null) => void;
  itemKey: (item: T) => string;
  itemLabel: (item: T) => string;
  isSelected: (item: T) => boolean;
  onSelect: (item: T) => void;
}

export function AccordionNav<T>({ groups, forceOpen, openGroup, onOpenGroup, itemKey, itemLabel, isSelected, onSelect }: AccordionNavProps<T>) {
  return (
    <>
      {groups.map((group) => {
        const open = Boolean(forceOpen) || openGroup === group.name;
        return (
          <div key={group.name}>
            <button type="button" className="nav-group" aria-expanded={open} onClick={() => onOpenGroup(open ? null : group.name)}>
              <span>{group.name}</span>
              <span className="mono" aria-hidden="true">
                {group.items.length} {open ? '−' : '+'}
              </span>
            </button>
            {open &&
              group.items.map((item) => (
                <button key={itemKey(item)} type="button" className="nav-item" aria-pressed={isSelected(item)} onClick={() => onSelect(item)}>
                  {itemLabel(item)}
                </button>
              ))}
          </div>
        );
      })}
    </>
  );
}
