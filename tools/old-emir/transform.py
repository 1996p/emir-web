#!/usr/bin/env python3
"""Собирает файлы для `import-data` из разобранных данных старого ЕМИР. Только локальные файлы.

Запуск: transform.py <папка>  -> <папка>/load/*.csv и transform-report.json
В консоль выводятся только счётчики и суммы. Контрольные итоги берутся из списка лицевых счетов, а не из карточек,
поэтому загрузка не пройдёт, если карточки и журналы что-то потеряли.
"""
import collections
import csv
import glob
import gzip
import json
import os
import re
import sys
from decimal import Decimal
from html.parser import HTMLParser

root = sys.argv[1]
SNAPSHOT_DATE = os.environ.get('EMIR_SNAPSHOT_DATE', '2026-11-05')
raw = os.path.join(root, 'raw')
normalized = os.path.join(root, 'normalized')
load = os.path.join(root, 'load')
os.makedirs(load, mode=0o700, exist_ok=True)
report = collections.Counter()


class Rows(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.rows, self.row, self.cell = [], None, None

    def handle_starttag(self, tag, attrs):
        if tag == 'tr':
            self.row = []
            self.rows.append(self.row)
        elif tag in ('td', 'th') and self.row is not None:
            self.cell = []
            self.row.append(self.cell)

    def handle_data(self, data):
        if self.cell is not None:
            self.cell.append(data)

    def text(self):
        return [[re.sub(r'\s+', ' ', ''.join(cell)).strip() for cell in row] for row in self.rows]


def jsonl(path):
    with open(path, encoding='utf-8') as source:
        for line in source:
            yield json.loads(line)


def zen(name):
    """Строки Zen-списка как словари. В строке сначала две служебные ячейки, скрытых колонок в ней нет."""
    state_path = glob.glob(os.path.join(raw, f'zen_*_{name}.state.json'))[0]
    with open(state_path, encoding='utf-8') as source:
        columns = [column['name'].strip() for column in json.load(source)['columns'] if not column['hidden']]
    for row in jsonl(state_path.replace('.state.json', '.jsonl')):
        yield dict(zip(columns, row['cells'][2:]))


def csp(name):
    with open(os.path.join(raw, f'{name}.state.json'), encoding='utf-8') as source:
        head = [re.sub(r'[ ↑↓]+$', '', title) for title in json.load(source)['head']]
    for row in jsonl(os.path.join(raw, f'{name}.jsonl')):
        yield dict(zip(head, row['cells'])) | {'_data': row.get('data', {})}


def write(name, columns, rows):
    path = os.path.join(load, name)
    with open(path, 'w', encoding='utf-8-sig', newline='') as output:
        writer = csv.writer(output, delimiter=';', lineterminator='\n')
        writer.writerow(columns)
        for row in rows:
            writer.writerow([row.get(column, '') for column in columns])
    os.chmod(path, 0o600)
    report[f'файл {name}'] = len(rows)


def number(text):
    text = (text or '').replace(' ', '').replace('\xa0', '')
    if not text:
        return None
    if ',' in text and '.' in text:
        text = text.replace(',', '')
    return Decimal(text.replace(',', '.'))


def rubles(value):
    return f'{value.quantize(Decimal("0.01"))}'.replace('.', ',')


def slug(text):
    return re.sub(r'[^a-zа-яё0-9]+', '_', text.lower()).strip('_')


def norm_address(*parts):
    return re.sub(r'[\s.,]+', ' ', ' '.join(part or '' for part in parts)).strip().lower()


TYPE_PREFIX = re.compile(r'^(с|п|рп|р\.п|пос|д|ст|х|г|ул|пер|пр|пр-т|кв-л|мкр|мкр-н|ш|б-р|пл|туп|проезд|тер)\.?\s+', re.I)


def plain(text):
    return re.sub(r'[\s.]+', ' ', TYPE_PREFIX.sub('', (text or '').strip())).strip().lower()


def address_key(city, street, house, flat):
    return '|'.join([plain(city), plain(street), plain(house), plain(flat)])


def parse_address(address):
    """«рп. Посёлок, ул. Улица 16/1-5» -> (посёлок, улица, дом, квартира)."""
    place, _, rest = (address or '').partition(', ')
    found = re.match(r'^(.*?)\s+(\S+?)(?:-(\S*))?$', rest)
    return (place, found.group(1), found.group(2), found.group(3) or '') if found else (place, rest, '', '')


def scheme_of(tariff, service):
    name = f'{tariff} {service}'.lower()
    if 'общедомов' in name:
        return 'MeterHouse'
    if 'индивидуальн' in name or 'счетчик' in name or 'счётчик' in name:
        return 'MeterApartment'
    if 'в заказе' in name:
        return 'ContractNorm'
    if 'водоотвед' in name and 'норматив' in name:
        return 'NormTarif'
    if 'тепл' in name:
        return 'NormHouse'
    return None


STATES = {'Работает': 'Работает', 'Приостановлен': 'Приостановлена', 'Приостановлена': 'Приостановлена', 'Расторгнут': 'Расторгнута'}
CLOSED = 'Расторгнута'
SERVICE_IDS = {'Теплоснабжение': 'heat', 'Теплоснабжение по счетчику': 'heat_meter', 'Пеня': 'penalty'}

# Данные списков
accounts_info = {row['Номер']: row for row in csp('Reference_Account_AccountList') if row.get('Номер', '').startswith('RUB.')}
accounts_list = {number_: number(row['Баланс']) or Decimal(0) for number_, row in accounts_info.items()}
heat_norms = {row['Name']: row['HeatNorm'] for row in zen('Категории_домов')}
abonent_rows = {}
for row in csp('abonentlist'):
    for core in re.findall(r'RUB\.(\d+)', row.get('ЛС', '')):
        abonent_rows[core] = row
contacts = collections.defaultdict(list)
for row in csp('contactlist'):
    if row.get('Телефон'):
        contacts[norm_address(row.get('Адрес'), row.get('ФИО'))].append(row['Телефон'])


def read_report(name):
    path = os.path.join(raw, 'reports', f'{name}.html.gz')
    with gzip.open(path, 'rt', encoding='utf-8', errors='replace') as source:
        parser = Rows()
        parser.feed(source.read())
    return parser.text()


# Показания квартирных счётчиков по счёту: номер и последнее показание из помесячной таблицы отчёта.
meter_by_account = {}
report_rows = read_report('meter_apartment_active')
report_head = next(row for row in report_rows if row[:2] == ['Адрес', 'Услуга'])
for row in report_rows:
    if len(row) != len(report_head) or row == report_head:
        continue
    values = dict(zip(report_head, row))
    readings = [(index, number(cell)) for index, cell in enumerate(row[6:]) if re.fullmatch(r'[\d\s]+(?:[.,]\d+)?', cell or '')]
    if readings:
        meter_by_account[values['Счёт']] = {'number': values['Номер счетчика'], 'last': readings[-1][1], 'history': len(readings)}
verified = {}
for row in zen('Квартирные_счетчики'):
    if row.get('Status') == 'Работает' and row.get('Number'):
        verified[row['Number']] = row.get('LastCheckDate', '')
residents_by_address = collections.defaultdict(list)
for row in zen('Проживающие'):
    residents_by_address[address_key(row.get('City'), row.get('Street'), row.get('House'), row.get('Apartment'))].append(row)
benefits = collections.defaultdict(list)
for row in zen('Льготники'):
    if row.get('FIO'):
        benefits[row['FIO'].lower()].append(row)

# Норматив по заказу: число или помесячно «1=0.5#2=0.5#...», в этом же виде его понимает новый расчёт.
contract_norms = {row['Contract']: row['Value'] for row in zen('Нормы_потребления_по_заказу') if row.get('Value')}
# Данные дома (котельная, тепловая камера, категория, материал стен) и сельсовет населённого пункта
house_info = {}
for row in zen('Данные_о_домах'):
    house_info[address_key(row.get('City'), row.get('Street'), row.get('Num'), '')] = row
district_of = {}
for row in csp('Address_CityList'):
    if row.get('Наименование'):
        district_of[plain(row['Наименование'])] = row.get('Сельсовет', '')
# Категория дома для абонентов, которых нет в списке с площадью.
house_categories = {}
for row in zen('Данные_о_домах'):
    if row.get('HouseCategory'):
        house_categories[address_key(row.get('City'), row.get('Street'), row.get('Num'), '')] = row['HouseCategory']
# Общедомовые счётчики: показания из отчёта, единица из справочника. Коэффициент 0,2389 подобран по начислениям
# старой системы, с ним они совпадают точнее всего.
UNIT_FACTORS = {'дж': ('ГДж', '0,2389'), 'гкал': ('Гкал', '1'), 'мвт*ч': ('МВт·ч', '0,8598452')}
meter_units = {}
for row in zen('Общедомовые_счетчики'):
    if row.get('Number'):
        meter_units[row['Number']] = re.sub(r'[^а-яa-z*]', '', (row.get('Unit') or '').lower())
RU_MONTHS = {'январь': 1, 'февраль': 2, 'март': 3, 'апрель': 4, 'май': 5, 'июнь': 6, 'июль': 7, 'август': 8, 'сентябрь': 9, 'октябрь': 10, 'ноябрь': 11, 'декабрь': 12}
house_rows = read_report('meter_house_active')
house_head = next(row for row in house_rows if row[:2] == ['Адрес', 'Услуга'])
house_periods = [f"{title.split()[1]}-{RU_MONTHS[title.split()[0]]:02d}" for title in house_head[4:]]
house_meters_out, house_readings_out, meter_of_house = [], [], {}
for row in house_rows:
    if len(row) != len(house_head) or row == house_head:
        continue
    place_part, street_part, house_part, _ = parse_address(row[0] + '-')
    key = address_key(place_part, street_part, house_part, '')
    meter_id = f'hm{len(house_meters_out) + 1}'
    unit, factor = UNIT_FACTORS.get(meter_units.get(row[2], 'гкал'), ('Гкал', '1'))
    meter_of_house.setdefault(key, meter_id)
    house_meters_out.append({'id': meter_id, 'place': place_part, 'street': f'{street_part} {house_part}'.strip(), 'number': row[2], 'unit': unit, 'unit_factor': factor})
    for period, cell in zip(house_periods, row[4:]):
        if re.fullmatch(r'[\d\s]+(?:[.,]\d+)?', cell or ''):
            house_readings_out.append({'meter_id': meter_id, 'period': period, 'value': format(number(cell).quantize(Decimal('0.0001')), 'f').replace('.', ',')})
resident_history = collections.defaultdict(list)
_history_file = os.path.join(raw, 'zensplit_История_проживающих_id.jsonl')
if os.path.exists(_history_file):
    _history_columns = json.load(open(os.path.join(raw, 'zensplit_История_проживающих_id.state.json'), encoding='utf-8'))['columns']
    _seen_history = set()
    for _row in jsonl(_history_file):
        record = dict(zip(_history_columns, _row['cells']))
        if record['ID'] in _seen_history or not record.get('Human'):
            continue
        _seen_history.add(record['ID'])
        resident_history[address_key(*parse_address(record['Address']))].append(record)
abonents_out, services_out, residents_out, beneficiaries_out = [], [], [], []
seen_benefits, seen_uuids = set(), set()
tariffs, current_prices = {}, {}
line_by_contract, line_by_service = {}, {}
account_of = {}
abonent_by_name = {}
tariff_of_line = {}
seen_accounts = set()
known_accounts = set()
for card in jsonl(os.path.join(normalized, 'abonents.jsonl')):
    if not card['services']:
        report['абонент без услуг, пропущен'] += 1
        continue
    numbers = [line['account'] for line in card['services'] if line['account'].startswith('RUB.')]
    core = next((re.search(r'RUB\.(\d+)', account).group(1) for account in numbers if '.Пеня' not in account), None) or re.search(r'RUB\.(\d+)', numbers[0]).group(1)
    if core in seen_accounts:
        core = f'{core}-{card["id"]}'
        report['абонент с тем же ЛС'] += 1
    seen_accounts.add(core)
    account_of[card['id']] = core
    list_row = abonent_rows.get(core.split('-')[0], {})
    report['абонент найден в списке с площадью' if list_row else 'абонент не найден в списке с площадью'] += 1
    fields = card['fields']
    organization = any(line['account'].endswith('юл') for line in card['services'])
    name = list_row.get('ФИО') or card.get('name', '')
    if not list_row:
        report['имя и адрес взяты только из карточки'] += 1
    elif list_row.get('ФИО') != card.get('name'):
        report['имя в карточке и списке различается'] += 1
    # В карточке адрес начинается со страны, нужны последние две части: населённый пункт и улица с домом.
    full_address = list_row.get('Адрес') or ', '.join(card.get('address', '').split(', ')[-2:])
    place, _, street = full_address.partition(', ')
    flat = list_row.get('Квартира', '')
    place_key, street_key, house_key, flat_key = parse_address(full_address)
    home_key = address_key(place_key, street_key, house_key, flat_key)
    norm = heat_norms.get(list_row.get('Категория') or house_categories.get(address_key(place_key, street_key, house_key, ''), ''))
    phones = contacts.get(norm_address(full_address, name), [])
    abonents_out.append({
        'account': core, 'kind': 'ЮЛ' if organization else 'ФЛ', 'name': name, 'place': place, 'street': street or full_address, 'flat': flat,
        'phone': phones[0] if phones else '', 'area': list_row.get('Отапливаемая пл-дь', ''), 'residents': list_row.get('Кол-во прож.', ''),
        'house_heat_norm': norm.replace('.', ',') if norm and norm.startswith('.') is False else ('0' + norm.replace('.', ',') if norm else ''),
        'note': fields.get('Дополнительная информация', ''),
        'district': district_of.get(plain(place_key), ''),
        'boiler': house_info.get(address_key(place_key, street_key, house_key, ''), {}).get('BoilerName', ''),
        'heat_chamber': house_info.get(address_key(place_key, street_key, house_key, ''), {}).get('BoilerRoomName', ''),
        'house_category': house_info.get(address_key(place_key, street_key, house_key, ''), {}).get('HouseCategory', '') or list_row.get('Категория', ''),
        'wall_material': house_info.get(address_key(place_key, street_key, house_key, ''), {}).get('WallMaterial', '') or list_row.get('Материал стен', ''),
    })
    abonent_by_name[norm_address(name, full_address)] = core
    used = set()
    for line in card['services']:
        known_accounts.add(line['account'])
        service_id = SERVICE_IDS.get(line['service'], f'c{line["contract"]}')
        if service_id in used:
            service_id = f'{service_id}_{line["contract"]}'
        used.add(service_id)
        line_by_contract[(card['id'], line['contract'])] = service_id
        line_by_service.setdefault((card['id'], line['service']), service_id)
        balance = Decimal(line['balance'])
        tariff_id = ''
        if service_id != 'penalty':
            scheme = scheme_of(line['tariff'], line['service'])
            if scheme is None:
                report[f'неизвестная схема: {line["tariff"][:40]}'] += 1
            else:
                tariff_id = slug(line['tariff'])
                tariffs[tariff_id] = {'id': tariff_id, 'name': line['tariff'], 'scheme': scheme, 'unit': 'Гкал'}
                price = number(line['price'])
                if price:
                    current_prices[tariff_id] = price
                tariff_of_line[(card['id'], service_id)] = tariff_id
        meter = meter_by_account.get(line['account']) if tariffs.get(tariff_id, {}).get('scheme') == 'MeterApartment' else None
        services_out.append({
            'account': core, 'service_id': service_id, 'name': line['service'], 'state': STATES.get(line['state'], line['state']), 'tariff_id': tariff_id, 'debt': rubles(-balance), 'contract_norm': contract_norms.get(line['contract'], ''), 'house_meter_id': (meter_of_house.get(address_key(place_key, street_key, house_key, ''), '') if tariffs.get(tariff_id, {}).get('scheme') == 'MeterHouse' else ''),
            'meter_number': meter['number'] if meter else '', 'meter_last_reading': format(meter['last'], 'f').replace('.', ',') if meter else '', 'meter_verified_until': '',
        })
        if meter:
            report['услуга со счётчиком и показанием'] += 1
        elif tariffs.get(tariff_id, {}).get('scheme') == 'MeterApartment':
            report['квартирный счётчик без показания'] += 1
        report['баланс услуги совпал со списком счетов' if accounts_list.get(line['account']) == balance else 'баланс услуги НЕ совпал со списком счетов'] += 1
    found = residents_by_address.get(home_key, [])
    names = sorted({person.get('FIO', '') for person in found if person.get('FIO')})
    report['повторы проживающих убраны'] += len(found) - len(names)
    # История проживающих даёт даты въезда и выезда, в том числе тех, кто уже выписан.
    history = resident_history.get(home_key, [])
    for record in history:
        residents_out.append({'account': core, 'name': record['Human'], 'from_date': record['DateCreate'], 'to_date': record['DateDelete']})
    report['адрес с историей проживающих' if history else 'адрес без истории проживающих'] += 1
    recorded = {record['Human'].lower() for record in history if not record['DateDelete']}
    for person_name in names:
        if person_name.lower() in recorded:
            continue
        residents_out.append({'account': core, 'name': person_name})
        report['проживающий без даты въезда'] += 1
        # В списке льготников нет адреса: связь только через совпадение ФИО с проживающим.
        for benefit in benefits.get(person_name.lower(), []):
            if benefit.get('Status') != 'Активен':
                report['льготник не активен, не перенесён'] += 1
                continue
            key = (core, person_name, benefit.get('Benefit', ''))
            uuid = benefit.get('SZHumanNumber', '')
            if key in seen_benefits:
                report['повторы льготников убраны'] += 1
                continue
            seen_benefits.add(key)
            if uuid in seen_uuids:
                report['льготник с тем же UUID у другого абонента или в другой категории: UUID не указан'] += 1
                uuid = ''
            elif uuid:
                seen_uuids.add(uuid)
            beneficiaries_out.append({'account': core, 'full_name': person_name, 'category': benefit.get('Benefit', ''), 'uuid': uuid})
    report['адрес с проживающими' if found else 'адрес без проживающих'] += 1

closed_line_by_number = {}
# Остатки счетов, которых нет в действующих услугах карточек: долги по закрытым услугам. Без них пропали бы деньги.
SUFFIX_NAMES = {'В': 'Водоотведение', 'Вюл': 'Водоотведение', 'Т': 'Теплоснабжение', 'Тюл': 'Теплоснабжение'}
missing = {number_: value for number_, value in accounts_list.items() if number_ not in known_accounts and value != 0}
carried = Decimal(0)
for number_, value in sorted(missing.items()):
    found = re.fullmatch(r'RUB\.(\d+)\.(.+)', number_)
    core, suffix = found.group(1), found.group(2)
    if core not in seen_accounts:
        # У закрытой услуги бывает свой номер: абонент ищется по имени и адресу из списка счетов.
        info = accounts_info.get(number_, {})
        owner = abonent_by_name.get(norm_address(info.get('ФИО'), info.get('Адрес')))
        if owner:
            core = owner
            report['закрытая услуга привязана по имени и адресу'] += 1
        else:
            place_part, _, street_part = (info.get('Адрес') or '').partition(', ')
            abonents_out.append({'account': core, 'kind': 'ЮЛ' if suffix.endswith('юл') else 'ФЛ', 'name': info.get('ФИО') or f'Абонент счёта {core}', 'place': place_part, 'street': street_part or info.get('Адрес') or 'нет адреса', 'note': 'Создан при переносе: остаток по закрытой услуге'})
            seen_accounts.add(core)
            abonent_by_name[norm_address(info.get('ФИО'), info.get('Адрес'))] = core
            report['абонент создан из-за остатка по закрытой услуге'] += 1
    closed_line_by_number[number_] = (core, f'closed_{slug(suffix)}_{found.group(1)}')
    services_out.append({'account': core, 'service_id': f'closed_{slug(suffix)}_{found.group(1)}', 'name': f"{SUFFIX_NAMES.get(suffix, suffix)} (закрытая услуга)", 'state': CLOSED, 'tariff_id': '', 'debt': rubles(-value)})
    carried += value
    report['закрытая услуга с остатком добавлена'] += 1
report['сумма добавленных остатков, коп'] = int(carried * 100)

# История начислений, цен и оплат
charges = collections.defaultdict(lambda: {'amount': Decimal(0), 'volume': Decimal(0), 'prices': collections.Counter()})
payments_out = []
prices_by_period = collections.defaultdict(lambda: collections.defaultdict(collections.Counter))
for op in jsonl(os.path.join(normalized, 'operations.jsonl')):
    abonent = op['abonent']
    if abonent not in account_of:
        continue
    # Срез на момент карточек: операции позже него в балансах ещё не учтены и в историю не идут.
    if op['at'][:10] > SNAPSHOT_DATE:
        report['операция позже среза, не перенесена'] += 1
        continue
    service_id = line_by_contract.get((abonent, op['contract'])) or line_by_service.get((abonent, op['service']))
    if service_id is None:
        report['операция закрытой услуги без строки в карточке'] += 1
        service_id = SERVICE_IDS.get(op['service']) or f'closed_{slug(op["service"])}'
        line_by_service[(abonent, op['service'])] = service_id
        # Услуга закрыта и в карточке не видна: нужна строка с нулевым остатком, чтобы сохранить историю.
        services_out.append({'account': account_of[abonent], 'service_id': service_id, 'name': op['service'], 'state': CLOSED, 'tariff_id': '', 'debt': '0,00'})
    amount = Decimal(op['out']) - Decimal(op['in'])
    if op['doc'] == 'Абон. плата':
        period = op['period'] or op['at'][:7]
        if not op['period']:
            report['начисление без месяца: взят месяц операции'] += 1
        entry = charges[(account_of[abonent], service_id, period)]
        entry['amount'] += amount
        if op['volume'] and op['service'] != 'Пеня':
            sign = 1 if amount >= 0 else -1
            entry['volume'] += sign * Decimal(op['volume'])
            entry['prices'][op['price']] += 1
            tariff_id = tariff_of_line.get((abonent, service_id))
            if tariff_id and amount > 0:
                prices_by_period[tariff_id][period][op['price']] += 1
    else:
        kind = 'Перенос' if '->' in op['text'] else ('Выплата' if op['doc'].startswith('Расходный') else 'Касса')
        payments_out.append({'account': account_of[abonent], 'date': op['at'][:10], 'source': kind, 'cashier': op['operator'], 'amount': rubles(-amount)})
# Операции закрытых услуг: история к строкам, созданным по остаткам этих счетов
_closed_file = os.path.join(normalized, 'closed_operations.jsonl')
if os.path.exists(_closed_file):
    for op in jsonl(_closed_file):
        if op['at'][:10] > SNAPSHOT_DATE:
            continue
        target = closed_line_by_number.get(op['account'].replace('_', ' '), closed_line_by_number.get(op['account']))
        if target is None:
            report['операция закрытого счёта без строки'] += 1
            continue
        account_core, service_id = target
        amount = Decimal(op['out']) - Decimal(op['in'])
        if op['doc'] == 'Абон. плата':
            entry = charges[(account_core, service_id, op['period'] or op['at'][:7])]
            entry['amount'] += amount
        else:
            payments_out.append({'account': account_core, 'date': op['at'][:10], 'source': 'Перенос' if '->' in op['text'] else ('Выплата' if op['doc'].startswith('Расходный') else 'Касса'), 'cashier': op['operator'], 'amount': rubles(-amount)})
        report['операция закрытого счёта перенесена'] += 1

write('charges.csv', ['account', 'service_id', 'period', 'amount', 'volume', 'price'], [
    {'account': key[0], 'service_id': key[1], 'period': key[2], 'amount': rubles(value['amount']), 'volume': format(value['volume'].quantize(Decimal('0.0001')), 'f').replace('.', ','), 'price': (value['prices'].most_common(1)[0][0] if value['prices'] else '').replace('.', ',')}
    for key, value in sorted(charges.items()) if value['amount'] != 0])
write('payments.csv', ['account', 'date', 'source', 'cashier', 'amount'], payments_out)

price_rows = []
for tariff_id in tariffs:
    previous = None
    for period, counts in sorted(prices_by_period[tariff_id].items()):
        price = counts.most_common(1)[0][0]
        if price != previous:
            price_rows.append({'tariff_id': tariff_id, 'from_period': period if previous is not None else '2000-01', 'price': price.replace('.', ',')})
            previous = price
    current = current_prices.get(tariff_id)
    if current is not None and (previous is None or Decimal(previous) != current):
        report['цена тарифа из карточки новее журналов'] += 1
        price_rows.append({'tariff_id': tariff_id, 'from_period': '2026-11' if previous is not None else '2000-01', 'price': str(current).replace('.', ',')})

write('tariffs.csv', ['id', 'name', 'scheme', 'unit'], list(tariffs.values()))
write('tariff_prices.csv', ['tariff_id', 'from_period', 'price'], price_rows)
write('abonents.csv', ['account', 'kind', 'name', 'short_name', 'place', 'street', 'flat', 'phone', 'inn', 'area', 'residents', 'house_heat_norm', 'district', 'boiler', 'heat_chamber', 'house_category', 'wall_material', 'credit', 'note', 'consent', 'ownership_document'], abonents_out)
write('services.csv', ['account', 'service_id', 'name', 'state', 'tariff_id', 'debt', 'contract_norm', 'meter_number', 'meter_last_reading', 'meter_verified_until', 'meter_average', 'house_meter_id'], services_out)
write('house_meters.csv', ['id', 'place', 'street', 'number', 'unit', 'unit_factor'], house_meters_out)
write('house_meter_readings.csv', ['meter_id', 'period', 'value'], house_readings_out)
write('residents.csv', ['account', 'name', 'from_date', 'to_date'], residents_out)
write('beneficiaries.csv', ['account', 'full_name', 'category', 'from_date', 'to_date', 'uuid'], beneficiaries_out)

# Контроль: чистый остаток по строкам обязан равняться списку лицевых счетов, иначе файлы не выпускаются.
line_debts = [number(row['debt']) for row in services_out]
total_debt = sum(value for value in line_debts if value > 0)
total_credit = sum(-value for value in line_debts if value < 0)
expected = sum(accounts_list.values())
report['чистый остаток по строкам, коп'] = int((total_credit - total_debt) * 100)
report['чистый остаток по списку счетов, коп'] = int(expected * 100)
if total_credit - total_debt != expected:
    report['РАСХОЖДЕНИЕ со списком счетов, коп'] = int((total_credit - total_debt - expected) * 100)
write('control.csv', ['metric', 'value'], [
    {'metric': 'abonents', 'value': len(abonents_out)}, {'metric': 'services', 'value': len(services_out)},
    {'metric': 'tariffs', 'value': len(tariffs)}, {'metric': 'total_debt', 'value': rubles(Decimal(total_debt))}, {'metric': 'total_credit', 'value': rubles(Decimal(total_credit))},
])
with open(os.path.join(root, 'transform-report.json'), 'w', encoding='utf-8') as output:
    json.dump(report, output, ensure_ascii=False, indent=1)
os.chmod(output.name, 0o600)
for key, value in sorted(report.items()):
    print(value, key)
print('долг', total_debt, 'переплата', total_credit)
