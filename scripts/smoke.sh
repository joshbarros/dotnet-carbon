#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5218}"
RABBIT="${RABBIT:-http://127.0.0.1:15708}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }

ADMIN_EMAIL="${ADMIN_EMAIL:-admin@carbon.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-CarbonAdmin2026}"
CUSTOMER_PASSWORD="Smoke-Passw0rd1"
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
token_of() { python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])"; }

open_batch() { # code tonnesIssued -> prints nothing
  curl -sf -X POST "$API/api/batches" -H "$JSON" -H "$ADM" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Batch\",\"standard\":\"VCS\",\"country\":\"Ecuador\",\"tonnesIssued\":$2,\"priceCents\":1500}" >/dev/null \
    || fail "could not open batch $1"
}
book() { # code reference tonnes -> prints purchase id
  curl -sf -X POST "$API/api/purchases" -H "$JSON" -H "$CUS" \
    -d "{\"batchCode\":\"$1\",\"reference\":\"$2\",\"tonnes\":$3}" | jget "['id']"
}
purchase_field() { curl -sf -H "$ADM" "$API/api/purchases/$1" | jget "['$2']"; }
remaining() { curl -sf -H "$ADM" "$API/api/batches/$1" | jget "['tonnesLeft']"; }

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(purchase_field "$1" status)" = "$2" ]; }
remaining_is() { [ "$(remaining "$1")" = "$2" ]; }
terminal() { case "$(purchase_field "$1" status)" in Allocated|Rejected|Reversed) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf -H "$ADM" "$API/api/purchases/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/certificates.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "0. authentication and ownership"
[ "$(code_of "$API/api/batches")" = 401 ] || fail "an anonymous request should be 401"
[ "$(code_of -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"wrong-Passw0rd\"}")" = 401 ] || fail "a wrong password should be 401"
ADM="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" | token_of)" || fail "the administrator could not sign in"
CUS="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"smoke-$RUN@example.com\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)" || fail "could not register a customer"
OTHER="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"other-$RUN@example.com\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)"
[ "$(code_of -X POST "$API/api/batches" -H "$JSON" -H "$CUS" -d '{"code":"X"}')" = 403 ] || fail "a customer must not open batches"
open_batch "A-$RUN" 5
IDA="$(book "A-$RUN" "ORG-$RUN-OWN" 1)"
[ "$(code_of -H "$OTHER" "$API/api/purchases/$IDA")" = 404 ] || fail "another customer must get 404 for someone else's purchase"
[ "$(code_of -H "$CUS" "$API/api/purchases/$IDA/events")" = 403 ] || fail "the event log is staff only"
[ "$(code_of -X POST -H "$OTHER" "$API/api/purchases/$IDA/reversal")" = 404 ] || fail "another customer must not reverse it"
say "401 anonymous, 401 bad password, 403 customer opening a batch, 404 for another customer, 403 on the event log"

echo "1. input is validated at the door"
open_batch "V-$RUN" 10
for body in \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"\",\"tonnes\":1}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":0}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":501}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":4294967297}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":9223372036854775807}"; do
  code="$(code_of -X POST "$API/api/purchases" -H "$JSON" -H "$CUS" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing tonne counts are all 400"

echo "2. happy path"
open_batch "H-$RUN" 100
ID="$(book "H-$RUN" "ORG-$RUN-1" 10)"
wait_for "purchase to be Allocated" status_is "$ID" Allocated
[ "$(purchase_field "$ID" totalCents)" = 15200 ] || fail "total should be 10 t x (1500 + 20 levy) = 15200"
[ "$(curl -sf -H "$ADM" "$API/api/certificates/$ID" | jget "['status']")" = Issued ] || fail "certificate not issued"
wait_for "remaining tonnes to be 90" remaining_is "H-$RUN" 90
TYPES="$(event_types "$ID")"
for t in tonnes.reserve tonnes.held certificate.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "allocated, total 15200, remaining 90, Kafka log has the three steps"

echo "3. last tonne: three purchases, one commits, two are short and write no hold"
open_batch "L-$RUN" 1
TMP="$(mktemp -d)"
for n in A B C; do ( book "L-$RUN" "ORG-$RUN-$n" 1 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "purchase $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "purchase $i to settle" terminal "$i"; done
ALLOCATED=0; SHORT=0
for i in $IDS; do
  s="$(purchase_field "$i" status)"
  if [ "$s" = Allocated ]; then ALLOCATED=$((ALLOCATED + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(purchase_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *tonnes.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$ALLOCATED" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 allocated and 2 short, got $ALLOCATED allocated and $SHORT short"
[ "$(remaining "L-$RUN")" = 0 ] || fail "remaining should be 0"
say "1 allocated, 2 short, remaining 0, no hold for the losers"

echo "4. poison: committed as rejected, dead-lettered, the tonnes come back"
open_batch "P-$RUN" 5
BEFORE="$(dlq_count)"
IDP="$(book "P-$RUN" "ORG-POISON-$RUN" 2)"
wait_for "poison purchase to be Rejected" status_is "$IDP" Rejected
wait_for "tonnes to be restored to 5" remaining_is "P-$RUN" 5
wait_for "certificates.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf -H "$ADM" "$API/api/certificates/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the certificate row"
[ "$(purchase_field "$IDP" tonnesReleasedAt)" != None ] || fail "purchase should record the release"
say "rejected, dead-lettered (+1 in certificates.work.dlq), remaining back to 5"

echo "5. reversal races the final mark: the reversal wins, the issued certificate stays"
open_batch "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "ORG-$RUN-R$attempt" 2)"
  # The certificate exists before the purchase is marked Allocated (the final mark is delayed in compose).
  wait_for "certificate to exist" curl -sf -H "$ADM" "$API/api/certificates/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (purchase already Allocated); the reversal still has to hold; retrying"
  curl -sf -X POST -H "$CUS" "$API/api/purchases/$IDR/reversal" >/dev/null || true
  wait_for "purchase to be Reversed" status_is "$IDR" Reversed
done
[ "$WON" = 1 ] || fail "never caught the purchase between certificate issued and final mark"
curl -sf -X POST -H "$CUS" "$API/api/purchases/$IDR/reversal" >/dev/null || fail "reversal was refused"
wait_for "purchase to be Reversed" status_is "$IDR" Reversed
sleep 3 # longer than the final-mark delay: the late mark must not undo the reversal
status_is "$IDR" Reversed || fail "the final mark overwrote the reversal"
[ "$(curl -sf -H "$ADM" "$API/api/certificates/$IDR" | jget "['status']")" = Issued ] || fail "the issued certificate was rolled back"
[ "$(purchase_field "$IDR" certificateNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "tonnes to come back" remaining_is "R-$RUN" 6
code="$(code_of -X POST -H "$CUS" "$API/api/purchases/$IDR/reversal")"
[ "$code" = 409 ] || fail "second reversal should be 409, got $code"
say "purchase Reversed, certificate still Issued, remaining 6, second reversal 409"

echo "carbon smoke passed"
