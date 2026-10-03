#!/usr/bin/env sh
# Шаг деплоя для любого CI: вызывает VDS Service Updater и завершается кодом по результату.
#
#   UPDATER_URL    адрес апдейтера, например https://deploy.example.com   (обязательно)
#   IMAGE          образ с тегом, например ghcr.io/myorg/app:1.4.2         (обязательно)
#   UPDATER_TOKEN  значение X-Webhook-Token (если защита включена)
#   DRY_RUN        true: только показать, что изменится
#   TIMEOUT        максимум секунд ожидания ответа (по умолчанию 300; больше WaitTimeoutSeconds апдейтера)
#
# Коды выхода: 0 успех (HTTP 200), 1 деплой отклонён или не удался, 2 нет связи с апдейтером.
set -eu

: "${UPDATER_URL:?Укажите UPDATER_URL}"
: "${IMAGE:?Укажите IMAGE}"
DRY_RUN="${DRY_RUN:-false}"
TIMEOUT="${TIMEOUT:-300}"

# Образ попадает в JSON без экранирования, поэтому допускаем только безопасные символы.
case "$IMAGE" in
  *[!A-Za-z0-9._:/@-]*) echo "Недопустимые символы в IMAGE: $IMAGE" >&2; exit 2 ;;
esac
case "$DRY_RUN" in true|false) ;; *) echo "DRY_RUN должен быть true или false" >&2; exit 2 ;; esac

response="$(mktemp)"
trap 'rm -f "$response"' EXIT

set -- -H 'Content-Type: application/json'
if [ -n "${UPDATER_TOKEN:-}" ]; then set -- "$@" -H "X-Webhook-Token: $UPDATER_TOKEN"; fi

echo "Деплой $IMAGE (dryRun=$DRY_RUN) через ${UPDATER_URL%/}"
code="$(curl -sS -o "$response" -w '%{http_code}' --connect-timeout 10 --max-time "$TIMEOUT" \
  -X POST "${UPDATER_URL%/}/api/v1/deploy" "$@" \
  -d "{\"image\":\"$IMAGE\",\"dryRun\":$DRY_RUN}")" || {
  echo "Нет связи с апдейтером (или истёк TIMEOUT=${TIMEOUT}s). Деплой мог продолжиться: проверьте логи апдейтера." >&2
  exit 2
}

cat "$response"; echo
if command -v jq >/dev/null 2>&1; then
  jq -r '(.results // [])[] | "  \(.target)/\(.service): \(.status)\(if .code then " (" + .code + ")" else "" end)\(if .message then " - " + .message else "" end)"' "$response" 2>/dev/null || true
fi

case "$code" in
  200) echo "Успех (HTTP 200)"; exit 0 ;;
  401) echo "Неверный или отсутствующий токен (HTTP 401)" >&2 ;;
  403) echo "Образ не разрешён или все сервисы защищены (HTTP 403)" >&2 ;;
  404) echo "Сервис с таким образом не найден (HTTP 404)" >&2 ;;
  409) echo "В этот стек уже идёт другой деплой (HTTP 409), повторите позже" >&2 ;;
  429) echo "Слишком много запросов (HTTP 429)" >&2 ;;
  502|503|504) echo "Прокси оборвал запрос (HTTP $code). Деплой мог продолжиться: проверьте логи апдейтера и proxy_read_timeout." >&2 ;;
  *) echo "Деплой не удался (HTTP $code)" >&2 ;;
esac
exit 1
