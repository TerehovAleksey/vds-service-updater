#!/usr/bin/env bash
# Сквозной тест compose-режима на реальном Docker.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

STACK_DIR="$WORK/compose"
APP_URL="http://127.0.0.1:18081/"
cleanup() {
  docker compose -f "$STACK_DIR/compose.yml" -p e2e down -v >/dev/null 2>&1 || true
  lib_cleanup
}
trap cleanup EXIT

rm -rf "$WORK"; mkdir -p "$STACK_DIR"
start_registry
build_images

cat > "$STACK_DIR/compose.yml" <<YAML
# комментарий должен пережить деплой
services:
  app:
    image: $REG/app:1   # тег меняет апдейтер
    ports: ["127.0.0.1:18081:80"]
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://127.0.0.1/"]
      interval: 2s
      timeout: 2s
      retries: 5
  db:
    image: $REG/db:1
YAML

log "Запуск тестового стека"
docker compose -f "$STACK_DIR/compose.yml" -p e2e up -d --wait
assert_eq "app-v1" "$(curl -fsS "$APP_URL")" "стек работает на app:1"

start_updater \
  -e "Updater__Targets__0__Name=compose" -e "Updater__Targets__0__Type=Compose" \
  -e "Updater__Targets__0__File=$STACK_DIR/compose.yml" -e "Updater__Targets__0__StackName=e2e" \
  -e "Updater__Targets__0__Protected__0=db"

log "Без токена"
assert_eq 401 "$(deploy "$REG/app:2" "")" "401 без токена"

log "Dry run"
assert_eq 200 "$(post_json "{\"image\":\"$REG/app:2\",\"dryRun\":true}")" "dry run: 200"
assert_body_has '"dry_run"' "dry run: статус dry_run"
assert_file_has "$STACK_DIR/compose.yml" "app:1" "dry run: файл не изменён"

log "Успешное обновление app:1 -> app:2"
assert_eq 200 "$(deploy "$REG/app:2")" "обновление: 200"
assert_body_has '"updated"' "обновление: статус updated"
assert_file_has "$STACK_DIR/compose.yml" "app:2   # тег меняет апдейтер" "файл: тег и комментарий сохранены"
assert_file_has "$STACK_DIR/compose.yml" "# комментарий должен пережить деплой" "файл: остальные комментарии сохранены"
wait_for "[ \"\$(curl -fsS $APP_URL)\" = app-v2 ]" 30 && pass "контейнер отдаёт app-v2" || fail "контейнер не перешёл на app-v2"

log "Повтор того же тега"
assert_eq 200 "$(deploy "$REG/app:2")" "повтор: 200"
assert_body_has '"unchanged"' "повтор: статус unchanged"

log "Защищённый сервис"
assert_eq 403 "$(deploy "$REG/db:2")" "защищённый db: 403"
assert_body_has 'service_protected' "защищённый db: код service_protected"
assert_file_has "$STACK_DIR/compose.yml" "db:1" "защищённый db: файл не изменён"

log "Неизвестный образ"
assert_eq 404 "$(deploy "$REG/nothing:1")" "неизвестный образ: 404"

log "Откат при неудаче (контейнер сразу падает)"
assert_eq 500 "$(deploy "$REG/app:bad")" "сломанный образ: 500"
assert_body_has 'rolled_back' "сломанный образ: rolled_back"
assert_file_has "$STACK_DIR/compose.yml" "app:2" "сломанный образ: файл восстановлен"
wait_for "[ \"\$(curl -fsS $APP_URL)\" = app-v2 ]" 60 && pass "после отката снова отдаётся app-v2" || fail "после отката сервис не работает"

log "Бэкапы"
backups="$(docker exec vds-e2e-updater sh -c 'ls /data/backups/compose | wc -l')"
if [ "$backups" -ge 2 ]; then pass "бэкапов: $backups"; else fail "ожидалось >= 2 бэкапов, найдено $backups"; fi

finish
