#!/usr/bin/env bash
# Общие функции сквозных тестов. Требуются: docker (с compose-плагином), curl, bash >= 4.
# Поднимает локальный registry (localhost:5000), собирает тестовые образы, собирает и запускает апдейтер из Dockerfile.
set -euo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$E2E_DIR/.." && pwd)"
WORK="${E2E_WORK:-/tmp/vds-e2e}"
REGISTRY_PORT="${E2E_REGISTRY_PORT:-5000}"
UPDATER_PORT="${E2E_UPDATER_PORT:-18080}"
TOKEN="e2e-token-0123456789abcdef"
REG="localhost:${REGISTRY_PORT}/e2e"
UPDATER_IMAGE="vds-service-updater:e2e"
FAILS=0

log()  { echo "==> $*"; }
pass() { echo "  ok:   $1"; }
fail() { echo "  FAIL: $1"; FAILS=$((FAILS + 1)); }

assert_eq()       { if [ "$1" = "$2" ]; then pass "$3"; else fail "$3 (ожидалось '$1', получено '$2')"; fi; }
assert_body_has() { if grep -q -- "$1" "$WORK/body.json"; then pass "$2"; else fail "$2 (тело ответа: $(cat "$WORK/body.json"))"; fi; }
assert_file_has() { if grep -q -- "$2" "$1"; then pass "$3"; else fail "$3 (в $1 нет '$2')"; fi; }
assert_file_lacks() { if grep -q -- "$2" "$1"; then fail "$3 ('$2' найдено в $1)"; else pass "$3"; fi; }

# wait_for '<команда>' [секунд]: повторяет команду раз в секунду до успеха
wait_for() {
  local cmd="$1" timeout="${2:-60}" i
  for ((i = 0; i < timeout; i++)); do
    if eval "$cmd" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  return 1
}

start_registry() {
  log "Локальный registry на localhost:${REGISTRY_PORT}"
  docker rm -f vds-e2e-registry >/dev/null 2>&1 || true
  docker run -d --name vds-e2e-registry -p "127.0.0.1:${REGISTRY_PORT}:5000" registry:2 >/dev/null
  wait_for "curl -fsS http://127.0.0.1:${REGISTRY_PORT}/v2/" 30
}

build_web_image() { # тег, текст страницы
  docker build -q -t "$1" - >/dev/null <<DOCKERFILE
FROM nginx:alpine
RUN echo "$2" > /usr/share/nginx/html/index.html
DOCKERFILE
  docker push -q "$1" >/dev/null
}

build_images() {
  log "Сборка тестовых образов"
  local v
  for v in 1 2 3; do build_web_image "$REG/app:$v" "app-v$v"; done
  for v in 1 2; do build_web_image "$REG/db:$v" "db-v$v"; done
  # «Сломанный» образ: контейнер сразу завершается с ошибкой
  docker build -q -t "$REG/app:bad" - >/dev/null <<'DOCKERFILE'
FROM alpine:3
CMD ["sh", "-c", "echo crashing; exit 1"]
DOCKERFILE
  docker push -q "$REG/app:bad" >/dev/null
}

# start_updater <дополнительные аргументы docker run: -e Updater__Targets__0__...>
start_updater() {
  log "Сборка и запуск апдейтера"
  docker build -q -t "$UPDATER_IMAGE" "$REPO_DIR" >/dev/null
  docker rm -f vds-e2e-updater >/dev/null 2>&1 || true
  docker run -d --name vds-e2e-updater \
    -p "127.0.0.1:${UPDATER_PORT}:8080" \
    -v /var/run/docker.sock:/var/run/docker.sock \
    -v "$WORK:$WORK" \
    -e "Updater__Auth__Token=$TOKEN" \
    -e "Updater__Deploy__AllowedImagePrefixes__0=$REG/" \
    -e "Updater__Deploy__WaitTimeoutSeconds=90" \
    -e "Updater__Deploy__StabilitySeconds=5" \
    "$@" "$UPDATER_IMAGE" >/dev/null
  wait_for "curl -fsS http://127.0.0.1:${UPDATER_PORT}/health" 60 \
    || { docker logs vds-e2e-updater; echo "Апдейтер не стартовал"; exit 1; }
}

# post_json '<json>' [токен]: печатает HTTP-код, тело в $WORK/body.json. Токен "" = без заголовка.
post_json() {
  local tok="${2-$TOKEN}" hdr=()
  if [ -n "$tok" ]; then hdr=(-H "X-Webhook-Token: $tok"); fi
  curl -sS -o "$WORK/body.json" -w '%{http_code}' -X POST "http://127.0.0.1:${UPDATER_PORT}/api/v1/deploy" \
    -H 'Content-Type: application/json' ${hdr[@]+"${hdr[@]}"} -d "$1"
}
deploy() { post_json "{\"image\":\"$1\"}" "${2-$TOKEN}"; } # deploy <образ> [токен]

lib_cleanup() {
  if [ "$FAILS" -ne 0 ]; then
    echo "---- логи апдейтера ----"
    docker logs --tail 80 vds-e2e-updater 2>&1 || true
  fi
  docker rm -f vds-e2e-updater vds-e2e-registry >/dev/null 2>&1 || true
}

finish() {
  if [ "$FAILS" -eq 0 ]; then echo "Все проверки пройдены"; else echo "Провалено проверок: $FAILS"; fi
  [ "$FAILS" -eq 0 ]
}
