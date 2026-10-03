#!/usr/bin/env bash
# Сквозной тест swarm-режима (service update) на реальном Docker.
# Если swarm не активен, скрипт выполнит `docker swarm init` и в конце `docker swarm leave --force`.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

STACK_DIR="$WORK/swarm"
APP_URL="http://127.0.0.1:18082/"
SWARM_INIT_BY_US=0
cleanup() {
  docker stack rm e2e >/dev/null 2>&1 || true
  lib_cleanup
  if [ "$SWARM_INIT_BY_US" = 1 ]; then docker swarm leave --force >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

service_image() { docker service inspect e2e_app --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}'; }

rm -rf "$WORK"; mkdir -p "$STACK_DIR"
if [ "$(docker info --format '{{.Swarm.LocalNodeState}}')" != "active" ]; then
  log "Инициализация swarm"
  docker swarm init >/dev/null 2>&1 || docker swarm init --advertise-addr 127.0.0.1 >/dev/null
  SWARM_INIT_BY_US=1
fi
start_registry
build_images

cat > "$STACK_DIR/stack.yml" <<YAML
services:
  app:
    image: $REG/app:1
    ports: ["18082:80"]
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://127.0.0.1/"]
      interval: 2s
      timeout: 2s
      retries: 5
    deploy:
      replicas: 1
      update_config:
        failure_action: rollback
        monitor: 5s
  db:
    image: $REG/db:1
YAML

log "Запуск тестового стека"
docker stack deploy -c "$STACK_DIR/stack.yml" e2e >/dev/null
wait_for "[ \"\$(curl -fsS $APP_URL)\" = app-v1 ]" 90 && pass "стек работает на app:1" || fail "стек не поднялся"

start_updater \
  -e "Updater__Targets__0__Name=swarm" -e "Updater__Targets__0__Type=Stack" \
  -e "Updater__Targets__0__File=$STACK_DIR/stack.yml" -e "Updater__Targets__0__StackName=e2e" \
  -e "Updater__Targets__0__Protected__0=db"

log "Успешное обновление app:1 -> app:2"
assert_eq 200 "$(deploy "$REG/app:2")" "обновление: 200"
assert_body_has '"updated"' "обновление: статус updated"
assert_file_has "$STACK_DIR/stack.yml" "app:2" "файл обновлён"
case "$(service_image)" in *"/e2e/app:2"*) pass "сервис swarm использует app:2" ;; *) fail "сервис swarm использует '$(service_image)'" ;; esac
wait_for "[ \"\$(curl -fsS $APP_URL)\" = app-v2 ]" 30 && pass "сервис отдаёт app-v2" || fail "сервис не отдаёт app-v2"

log "Повтор того же тега"
assert_eq 200 "$(deploy "$REG/app:2")" "повтор: 200"
assert_body_has '"unchanged"' "повтор: unchanged"

log "Защищённый сервис"
assert_eq 403 "$(deploy "$REG/db:2")" "защищённый db: 403"

log "Откат при неудаче (swarm откатывает сам, апдейтер восстанавливает файл)"
assert_eq 500 "$(deploy "$REG/app:bad")" "сломанный образ: 500"
assert_body_has 'rolled_back' "сломанный образ: rolled_back"
assert_file_has "$STACK_DIR/stack.yml" "app:2" "сломанный образ: файл восстановлен"
case "$(service_image)" in *"/e2e/app:2"*) pass "после отката сервис на app:2" ;; *) fail "после отката сервис на '$(service_image)'" ;; esac

finish
