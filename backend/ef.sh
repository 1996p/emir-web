#!/bin/sh
# dotnet ef для этого проекта. На macOS и Linux инструмент оставляет папку с именем "bin\Debug",
# сборка считает её содержимым проекта и при каждом запуске копирует в вывод всё глубже.
# Пример: ./ef.sh migrations add ИмяИзменения
cd "$(dirname "$0")" || exit 1
dotnet ef "$@" --project Emir.Api
status=$?
rm -rf 'Emir.Api/bin\Debug'
exit $status
