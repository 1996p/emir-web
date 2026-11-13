#!/usr/bin/env python3
"""Собирает файлы для `import-data` прямо из базы старого ЕМИР, поднятой в IRIS. Только локальные файлы.

Запуск: iris_extract.py <папка>
  <папка>/export.xml       классы старой системы (схемы хранения)
  <папка>/sverka/*.jsonl   глобалы, выгруженные из IRIS построчно: {"id": …, "v": [поля], "sub": {…}}
Результат: <папка>/load/*.csv и <папка>/load/extract-report.txt. В консоль только счётчики и суммы.

Деньги старой системы лежат на счетах (AccountD), договор (услуга) платит со своего PayAccount. Долг услуги = −Balance.
Несколько договоров одного абонента могут платить с одного счёта (смена тарифа: «Теплоснабжение» и «по счётчику»),
тогда остаток счёта достаётся одной строке: работающему договору, иначе последнему.
"""
import collections
import csv
import json
import os
import re
import sys
from decimal import Decimal, ROUND_HALF_UP

root = sys.argv[1]
load_dir = os.path.join(root, 'load')
os.makedirs(load_dir, mode=0o700, exist_ok=True)
report = collections.Counter()
problems = []

# Услуги ЖКХ старой системы. Остальные договоры (перевод денег, пополнение, касса) служебные.
SERVICE_IDS = {'13': 'heat', '19': 'heat_meter', '22': 'penalty', '23': 'drain', '5': 'heat_lost'}
STATES = {'Работает': 'Работает', 'Приостановлен': 'Приостановлена', 'Расторгнут': 'Расторгнута', 'Смена тарифа': 'Работает'}
# Справочники типов адресов в выгрузку не попали (лежат в общей базе), типы определены по названиям
CITY_TYPES = {'1': 'г.', '2': 'с.', '3': 'п.', '4': 'д.', '7': 'р.п.'}
STREET_TYPES = {'1': 'ул.', '2': 'пер.'}
# Сколько Гкал в единице показаний счётчика: как в начислениях старой системы
UNIT_FACTORS = {'J': ('ГДж', '0,2389'), 'GKAL': ('Гкал', '1'), 'MWH': ('МВт·ч', '0,8598452')}


def layouts():
    cache = os.path.join(root, 'storage.json')
    if os.path.exists(cache):
        return json.load(open(cache, encoding='utf-8'))
    text = open(os.path.join(root, 'export.xml'), encoding='utf-8', errors='replace').read()
    result = {}
    for storage in re.finditer(r'<Storage name="[^"]*">(.*?)</Storage>', text, re.S):
        body = storage.group(1)
        location = re.search(r'<DataLocation>\^([^<]+)</DataLocation>', body)
        if not location:
            continue
        entry = {'vals': {}, 'subs': {}}
        for data in re.finditer(r'<Data name="[^"]*">(.*?)</Data>', body, re.S):
            values = {m.group(1): m.group(2) for m in re.finditer(r'<Value name="(\d+)">\s*<Value>([^<]*)</Value>', data.group(1))}
            subscript = re.search(r'<Subscript>([^<]*)</Subscript>', data.group(1))
            if subscript:
                entry['subs'][subscript.group(1).strip('"')] = values
            elif values:
                entry['vals'] = values
        result[location.group(1)] = entry
    json.dump(result, open(cache, 'w', encoding='utf-8'), ensure_ascii=False)
    return result


LAYOUTS = layouts()


def table(name):
    """Записи глобала ^<name> по его схеме хранения: {id: {свойство: значение, '_sub': {...}, '_id': id}}."""
    positions = LAYOUTS.get(name, {}).get('vals', {})
    rows = {}
    with open(os.path.join(root, 'sverka', name + '.jsonl'), encoding='utf-8') as source:
        for line in source:
            record = json.loads(line)
            values = record.get('v', [])
            row = {prop: (values[int(index) - 1] if int(index) - 1 < len(values) else '') for index, prop in positions.items()}
            row['_sub'] = record.get('sub', {})
            row['_id'] = str(record['id'])
            rows[str(record['id'])] = row
    return rows


def s(value):
    return '' if value is None else str(value).replace('\x00', '').strip()


def dec(value):
    text = s(value)
    return Decimal(text) if text else None


def rubles(value):
    return format(value.quantize(Decimal('0.01'), rounding=ROUND_HALF_UP), 'f').replace('.', ',')


def number_text(value, places='0.0001'):
    return format(value.quantize(Decimal(places), rounding=ROUND_HALF_UP).normalize(), 'f').replace('.', ',')


def write(name, columns, rows):
    path = os.path.join(load_dir, name)
    with open(path, 'w', encoding='utf-8-sig', newline='') as output:
        writer = csv.writer(output, delimiter=';', lineterminator='\n')
        writer.writerow(columns)
        for row in rows:
            writer.writerow([row.get(column, '') for column in columns])
    report[f'файл {name}'] = len(rows)


contracts = table('Contract.ContractD')
accounts = table('AccountD')
providers = table('Reference.AccountProviderD')
services = table('Contract.Tarif.ServiceD')
state_history = table('Contract.History.StateD')
state_names = table('Reference.ContractStateD')
tariff_history = table('Contract.History.TarifD')
tariffs_old = table('Contract.Tarif.TarifD')
schemes = table('GKH.ContracA908.AbstractSchemeD')
abonents_old = table('AbonentD')
humans = table('HumanD')
organizations = table('OrganizationD')
gkh_abonents = {s(row['Abonent']): row for row in table('GKH.User.AbonentD').values()}
addresses = table('Address.PAddressD')
houses = table('Address.PHouseD')
streets = table('Address.PStreetD')
cities = table('Address.PCityD')
apartments = {s(row['Address']): row for row in table('GKH.Housing.ApartmentD').values()}
house_data = {s(row['House']): row for row in table('GKH.Housing.HouseD').values()}
categories = table('GKH.Housing.HouseCategoryD')
contract_norms = {s(row['Contract']): s(row['Value']) for row in table('GKH.Krsk.ContractNormD').values()}
residents_old = table('GKH.User.ResidentD')
meters = table('GKH.Hard.MeterD')
units = table('Reference.UnitD')
links = collections.defaultdict(list)
for row in table('GKH.Hard.LinkD').values():
    links[s(row['Contract'])].append(s(row['Meter']))
# Общедомовой счётчик привязан к дому (подузел MeterHouse = id дома Address.PHouse) и к услуге, как в MeterHouse.GetMeterListByHouseId
house_meters_by = collections.defaultdict(list)
for meter_id, meter in meters.items():
    house_link = meter['_sub'].get('MeterHouse')
    if house_link and s(meter.get('Status')) == '1':
        house_meters_by[(s(house_link[0]), s(meter.get('Service')))].append(meter_id)
readings = collections.defaultdict(dict)
for row in table('GKH.Hard.ReadingD').values():
    if dec(row['Value']) is not None:
        readings[s(row['Meter'])][s(row['Period'])] = dec(row['Value'])
bill_records = table('BookKeeping.Docs.BillRecordD')


def scheme_of(tariff_id):
    tariff = tariffs_old.get(tariff_id)
    link = tariff['_sub'].get('Tarif') if tariff else None
    return s(schemes.get(s(link[0]), {}).get('Code')) if link else ''


def state_of(contract):
    record = state_history.get(s(contract['curStateHistory']))
    return STATES.get(s(state_names.get(s(record['State']) if record else '', {}).get('Name')), 'Расторгнута')


def address_of(address_id):
    """(населённый пункт, улица с домом, квартира, id дома) по адресу Address.PAddress."""
    address = addresses.get(address_id, {})
    house = houses.get(s(address.get('House')), {})
    street = streets.get(s(house.get('Street')), {})
    city = cities.get(s(street.get('City')), {})
    place = ' '.join(part for part in (CITY_TYPES.get(s(city.get('Type')), ''), s(city.get('Name'))) if part)
    house_number = s(house.get('Num')) + (f'/{s(house.get("NumB"))}' if s(house.get('NumB')) else '')
    street_name = ' '.join(part for part in (STREET_TYPES.get(s(street.get('Type')), 'ул.'), s(street.get('Name'))) if s(street.get('Name')))
    return place, f'{street_name} {house_number}'.strip(), s(address.get('Apartment')), s(address.get('House'))


def short_name(full):
    parts = full.split()
    if len(parts) < 2 or any(char in full for char in '"«'):
        return ''
    return f'{parts[0]} ' + ' '.join(f'{part[0]}.' for part in parts[1:3])


# Договоры ЖКХ по абонентам
by_abonent = collections.defaultdict(list)
for contract in contracts.values():
    if s(contract['Service']) in SERVICE_IDS:
        by_abonent[s(contract['Abonent'])].append(contract)

abonents_out, services_out, residents_out = [], [], []
tariffs_out, line_of_contract = {}, {}
house_meters_out, house_meter_of = [], {}
seen_accounts = set()
used_money_accounts = set()
for abonent_id, own in sorted(by_abonent.items(), key=lambda item: int(item[0]) if item[0].isdigit() else 0):
    gkh = gkh_abonents.get(abonent_id)
    if gkh is None:
        report['абонент без адреса ЖКХ, пропущен'] += 1
        problems.append(f'абонент {abonent_id}: нет записи GKH.User.Abonent, договоров {len(own)}')
        continue
    # Остаток каждого счёта достаётся одной строке: работающему договору, иначе последнему
    by_account = collections.defaultdict(list)
    for contract in own:
        by_account[s(contract['PayAccount'])].append(contract)
    owner_of_account = {}
    for account_id, group in by_account.items():
        working = [contract for contract in group if state_of(contract) != 'Расторгнута']
        owner_of_account[account_id] = max(working or group, key=lambda contract: int(contract['_id']))['_id']

    lines = []
    for contract in sorted(own, key=lambda item: int(item['_id'])):
        account_id = s(contract['PayAccount'])
        account = accounts.get(account_id, {})
        state = state_of(contract)
        balance = dec(account.get('Balance')) or Decimal(0)
        holds_money = owner_of_account[account_id] == contract['_id'] and account_id not in used_money_accounts
        debt = -balance if holds_money else Decimal(0)
        if state == 'Расторгнута' and debt.quantize(Decimal('0.01')) == 0:
            # Закрытый договор без остатка: его начисления идут к строке, которая держит счёт
            line_of_contract[contract['_id']] = ('account', account_id)
            report['закрытый договор без остатка не перенесён'] += 1
            continue
        if holds_money:
            used_money_accounts.add(account_id)
        lines.append((contract, account, state, debt, holds_money))

    if not lines:
        report['абонент без действующих услуг и долга, пропущен'] += 1
        continue

    main = next((line for line in lines if s(line[0]['Service']) != '22' and line[1].get('Number')), lines[0])
    core = s(main[1].get('Number')) or f'old{abonent_id}'
    if core in seen_accounts:
        core = f'{core}-{abonent_id}'
        report['повтор номера ЛС, добавлен номер абонента'] += 1
    seen_accounts.add(core)

    provider_name = s(providers.get(s(main[1].get('Provider')), {}).get('Name'))
    is_org = provider_name.startswith('ЮЛ')
    person = abonents_old.get(abonent_id, {})
    human = humans.get(s(person.get('Face')), {})
    organization = organizations.get(s(person.get('Org')), {})
    name = s(organization.get('Title')) if is_org and s(organization.get('Title')) else s(human.get('Name')) or s(organization.get('Title')) or f'Абонент {core}'
    place, street, flat, house_id = address_of(s(gkh['Address']))
    apartment = apartments.get(s(gkh['Address']), {})
    area = dec(apartment.get('LivingArea')) or dec(apartment.get('TotalArea')) or Decimal(0)
    house = house_data.get(house_id, {})
    category = categories.get(s(house.get('HouseCategory')), {})
    norm = dec(category.get('HeatNorm')) or dec(house.get('HeatNorm'))
    people = [humans.get(s(row['Human']), {}).get('Name') for row in residents_old.values() if s(row['Address']) == s(gkh['Address'])]
    people = sorted({s(person_name) for person_name in people if s(person_name)})
    abonents_out.append({
        'account': core, 'kind': 'ЮЛ' if is_org else 'ФЛ', 'name': name, 'short_name': '' if is_org else short_name(name),
        'place': place, 'street': street or 'нет адреса', 'flat': flat, 'phone': s(human.get('Phone')) or s(organization.get('Phone')),
        'inn': s(organization.get('INN')) if is_org else '', 'area': number_text(area, '0.01'), 'residents': str(len(people)),
        'house_heat_norm': number_text(norm, '0.00001') if norm else '', 'house_category': s(category.get('Name')),
    })
    if not place or not street:
        problems.append(f'ЛС {core}: неполный адрес «{place}, {street}»')
        report['неполный адрес'] += 1
    for person_name in people:
        residents_out.append({'account': core, 'name': person_name})

    used = set()
    for contract, account, state, debt, holds_money in lines:
        service_code = s(contract['Service'])
        service_id = SERVICE_IDS[service_code]
        if service_id in used:
            service_id = f'{service_id}_{contract["_id"]}'
        used.add(service_id)
        line_of_contract[contract['_id']] = ('line', core, service_id)
        if holds_money:
            line_of_contract[('account', s(contract['PayAccount']))] = ('line', core, service_id)
        history = tariff_history.get(s(contract['curTarifHistory']), {})
        tariff_id_old = s(history.get('Tarif'))
        tariff_id = ''
        scheme = ''
        if service_id != 'penalty' and tariff_id_old:
            scheme = scheme_of(tariff_id_old)
            tariff = tariffs_old.get(tariff_id_old, {})
            if scheme:
                tariff_id = f't{tariff_id_old}'
                tariffs_out[tariff_id] = {'id': tariff_id, 'name': s(tariff.get('Name')), 'scheme': scheme, 'unit': 'Гкал', 'price': dec(tariff.get('Price')) or Decimal(0)}
            else:
                report[f'тариф без схемы: {s(tariff.get("Name"))[:40]}'] += 1
        meter_number = meter_last = house_meter_id = ''
        active_meters = [meter_id for meter_id in links.get(contract['_id'], []) if s(meters.get(meter_id, {}).get('Status')) == '1']
        if scheme == 'MeterApartment' and active_meters:
            meter = meters[active_meters[0]]
            meter_number = s(meter.get('Number'))
            if readings[active_meters[0]]:
                meter_last = number_text(readings[active_meters[0]][max(readings[active_meters[0]])])
            if len(active_meters) > 1:
                report['у услуги несколько квартирных счётчиков, взят первый'] += 1
        elif scheme == 'MeterApartment':
            report['квартирный счётчик не найден'] += 1
        on_house = house_meters_by.get((house_id, service_code), [])
        if scheme in ('MeterHouse', 'MeterHouseContractNorm') and not on_house:
            report['общедомовой счётчик дома не найден'] += 1
            problems.append(f'ЛС {core}: нет работающего общедомового счётчика по услуге {service_code} в доме {place}, {street}')
        if len(on_house) > 1:
            report['в доме несколько общедомовых счётчиков, взят первый'] += 1
        if scheme in ('MeterHouse', 'MeterHouseContractNorm') and on_house:
            meter_id = on_house[0]
            if meter_id not in house_meter_of:
                meter = meters[meter_id]
                unit, factor = UNIT_FACTORS.get(s(units.get(s(meter.get('Unit')), {}).get('Code')), ('Гкал', '1'))
                house_meter_of[meter_id] = f'hm{meter_id}'
                house_meters_out.append({'id': f'hm{meter_id}', 'place': place, 'street': street, 'number': s(meter.get('Number')), 'unit': unit, 'unit_factor': factor})
            house_meter_id = house_meter_of[meter_id]
        services_out.append({
            'account': core, 'service_id': service_id, 'name': s(services.get(service_code, {}).get('Name')), 'state': state,
            'tariff_id': tariff_id, 'debt': rubles(debt), 'contract_norm': contract_norms.get(contract['_id'], ''),
            'meter_number': meter_number, 'meter_last_reading': meter_last, 'house_meter_id': house_meter_id,
        })

# Начисления из счетов-квитанций (в старой базе они есть только по выставленным счетам, в основном юрлицам)
charges = collections.defaultdict(lambda: {'amount': Decimal(0), 'volume': Decimal(0), 'price': ''})
for record in bill_records.values():
    target = line_of_contract.get(s(record['Contract']))
    if target and target[0] == 'account':
        target = line_of_contract.get(target)
    if not target:
        report['начисление по договору вне переноса'] += 1
        continue
    _, core, service_id = target
    period = s(record['Period'])
    entry = charges[(core, service_id, period)]
    entry['amount'] += dec(record['Value']) or Decimal(0)
    entry['volume'] += dec(record['Count']) or Decimal(0)
    entry['price'] = s(record['Price']).replace('.', ',')
write('charges.csv', ['account', 'service_id', 'period', 'amount', 'volume', 'price'], [
    {'account': key[0], 'service_id': key[1], 'period': key[2], 'amount': rubles(value['amount']), 'volume': number_text(value['volume']), 'price': value['price']}
    for key, value in sorted(charges.items()) if value['amount'] != 0])

# Цены тарифов: действующая цена тарифа старой системы. Истории цен в выгрузке нет, поэтому с 2000-01.
write('tariffs.csv', ['id', 'name', 'scheme', 'unit'], list(tariffs_out.values()))
write('tariff_prices.csv', ['tariff_id', 'from_period', 'price'], [
    {'tariff_id': tariff['id'], 'from_period': '2000-01', 'price': number_text(tariff['price'], '0.0001')} for tariff in tariffs_out.values()])

# Показания общедомовых счётчиков
house_readings_out = []
for meter_id, new_id in house_meter_of.items():
    for period, value in sorted(readings[meter_id].items()):
        house_readings_out.append({'meter_id': new_id, 'period': period, 'value': number_text(value)})
write('house_meters.csv', ['id', 'place', 'street', 'number', 'unit', 'unit_factor'], house_meters_out)
write('house_meter_readings.csv', ['meter_id', 'period', 'value'], house_readings_out)
write('abonents.csv', ['account', 'kind', 'name', 'short_name', 'place', 'street', 'flat', 'phone', 'inn', 'area', 'residents', 'house_heat_norm', 'district', 'boiler', 'heat_chamber', 'house_category', 'wall_material', 'credit', 'note', 'consent', 'ownership_document'], abonents_out)
write('services.csv', ['account', 'service_id', 'name', 'state', 'tariff_id', 'debt', 'contract_norm', 'meter_number', 'meter_last_reading', 'meter_verified_until', 'meter_average', 'house_meter_id'], services_out)
write('residents.csv', ['account', 'name', 'from_date', 'to_date'], residents_out)

# Контроль: суммы считаются заново по самим счетам старой базы, а не по строкам файлов.
# Счета абонентов ЖКХ с ненулевым остатком, не попавшие в перенос, означают потерю денег.
debt_total = credit_total = Decimal(0)
for account_id in used_money_accounts:
    debt = -(dec(accounts[account_id].get('Balance')) or Decimal(0))
    debt = debt.quantize(Decimal('0.01'), rounding=ROUND_HALF_UP)
    if debt > 0:
        debt_total += debt
    else:
        credit_total -= debt
lost = Decimal(0)
for contracts_of in by_abonent.values():
    for contract in contracts_of:
        account_id = s(contract['PayAccount'])
        if account_id in used_money_accounts:
            continue
        balance = (dec(accounts.get(account_id, {}).get('Balance')) or Decimal(0)).quantize(Decimal('0.01'))
        if balance != 0:
            lost += balance
            problems.append(f'счёт {s(accounts.get(account_id, {}).get("Number"))} договора {contract["_id"]}: остаток {balance} не перенесён')
report['остаток счетов, не попавших в перенос, коп'] = int(lost * 100)
write('control.csv', ['metric', 'value'], [
    {'metric': 'abonents', 'value': len(abonents_out)}, {'metric': 'services', 'value': len(services_out)},
    {'metric': 'tariffs', 'value': len(tariffs_out)}, {'metric': 'total_debt', 'value': rubles(debt_total)}, {'metric': 'total_credit', 'value': rubles(credit_total)},
])
report['долг, ₽'] = rubles(debt_total)
report['переплата, ₽'] = rubles(credit_total)

with open(os.path.join(load_dir, 'extract-report.txt'), 'w', encoding='utf-8') as output:
    for key, value in sorted(report.items(), key=lambda item: item[0]):
        output.write(f'{key}: {value}\n')
    output.write('\nПроблемы:\n' + '\n'.join(problems) + '\n')
for key, value in sorted(report.items(), key=lambda item: item[0]):
    print(f'{key}: {value}')
print(f'проблем: {len(problems)}, подробно в load/extract-report.txt')
