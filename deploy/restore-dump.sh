#!/bin/sh
# Разворачивает дамп в базу контейнера, удаляя всё, что в ней было, и создаёт администратора.
# Запуск: EMIR_ADMIN_PASSWORD='не короче 12 символов' deploy/restore-dump.sh имя-файла.dump
# Файл берётся из папки, подключённой к контейнеру базы (EMIR_DUMPS, по умолчанию deploy/dumps).
set -eu
dump="${1:?Укажите имя файла дампа}"
: "${EMIR_ADMIN_PASSWORD:?Задайте EMIR_ADMIN_PASSWORD}"
cd "$(dirname "$0")/.."
docker compose up -d db
docker compose stop api
docker compose exec -T db sh -c 'dropdb -U emir --if-exists --force emir && createdb -U emir emir'
docker compose exec -T db pg_restore -U emir -d emir --no-owner --no-privileges "/dumps/$dump"
docker compose run --rm -e EMIR_ADMIN_PASSWORD="$EMIR_ADMIN_PASSWORD" api create-admin admin "Администратор"
docker compose up -d api web
echo "Готово: http://localhost:${EMIR_PORT:-8080}"
