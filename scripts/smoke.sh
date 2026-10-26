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

open_batch() { # code tonnesIssued -> prints nothing
  curl -sf -X POST "$API/api/batches" -H "$JSON" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Batch\",\"standard\":\"VCS\",\"country\":\"Ecuador\",\"tonnesIssued\":$2,\"priceCents\":1500}" >/dev/null \
    || fail "could not open batch $1"
}
book() { # code reference tonnes -> prints purchase id
  curl -sf -X POST "$API/api/purchases" -H "$JSON" \
    -d "{\"batchCode\":\"$1\",\"reference\":\"$2\",\"tonnes\":$3}" | jget "['id']"
}
purchase_field() { curl -sf "$API/api/purchases/$1" | jget "['$2']"; }
remaining() { curl -sf "$API/api/batches/$1" | jget "['tonnesLeft']"; }

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
event_types() { curl -sf "$API/api/purchases/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/certificates.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "1. input is validated at the door"
open_batch "V-$RUN" 10
for body in \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"\",\"tonnes\":1}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":0}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":501}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":4294967297}" \
  "{\"batchCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"tonnes\":9223372036854775807}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/purchases" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing tonne counts are all 400"

echo "2. happy path"
open_batch "H-$RUN" 100
ID="$(book "H-$RUN" "ORG-$RUN-1" 10)"
wait_for "purchase to be Allocated" status_is "$ID" Allocated
[ "$(purchase_field "$ID" totalCents)" = 15200 ] || fail "total should be 10 t x (1500 + 20 levy) = 15200"
[ "$(curl -sf "$API/api/certificates/$ID" | jget "['status']")" = Issued ] || fail "certificate not issued"
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
[ "$(curl -sf "$API/api/certificates/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the certificate row"
[ "$(purchase_field "$IDP" tonnesReleasedAt)" != None ] || fail "purchase should record the release"
say "rejected, dead-lettered (+1 in certificates.work.dlq), remaining back to 5"

echo "5. reversal races the final mark: the reversal wins, the issued certificate stays"
open_batch "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "ORG-$RUN-R$attempt" 2)"
  # The certificate exists before the purchase is marked Allocated (the final mark is delayed in compose).
  wait_for "certificate to exist" curl -sf "$API/api/certificates/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (purchase already Allocated); the reversal still has to hold; retrying"
  curl -sf -X POST "$API/api/purchases/$IDR/reversal" >/dev/null || true
  wait_for "purchase to be Reversed" status_is "$IDR" Reversed
done
[ "$WON" = 1 ] || fail "never caught the purchase between certificate issued and final mark"
curl -sf -X POST "$API/api/purchases/$IDR/reversal" >/dev/null || fail "reversal was refused"
wait_for "purchase to be Reversed" status_is "$IDR" Reversed
sleep 3 # longer than the final-mark delay: the late mark must not undo the reversal
status_is "$IDR" Reversed || fail "the final mark overwrote the reversal"
[ "$(curl -sf "$API/api/certificates/$IDR" | jget "['status']")" = Issued ] || fail "the issued certificate was rolled back"
[ "$(purchase_field "$IDR" certificateNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "tonnes to come back" remaining_is "R-$RUN" 6
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/purchases/$IDR/reversal")"
[ "$code" = 409 ] || fail "second reversal should be 409, got $code"
say "purchase Reversed, certificate still Issued, remaining 6, second reversal 409"

echo "carbon smoke passed"
