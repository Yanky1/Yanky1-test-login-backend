#!/usr/bin/env bash
# Sends a signed request to the Solvelt internal marketplace API (docs/ORDERS.md).
#   ORDERS_INBOUND_SECRET=... scripts/post-signed.sh POST /api/internal/v1/orders order.json [base-url]
# Needs: bash, curl, openssl. Dev default base URL: https://localhost:5001 (use -k for the dev certificate via CURL_OPTS).
set -euo pipefail
method=${1:?method}; path=${2:?path (with query, exactly as sent)}; file=${3:-}; base=${4:-https://localhost:5001}
secret=${ORDERS_INBOUND_SECRET:?set ORDERS_INBOUND_SECRET}
ts=${TIMESTAMP_OVERRIDE:-$(date +%s)}; nonce=${NONCE_OVERRIDE:-$(openssl rand -hex 16)}
if [ -n "$file" ]; then body=$(cat "$file"); else body=""; fi
hash=$(printf '%s' "$body" | openssl dgst -sha256 -hex | awk '{print $NF}')
canonical=$(printf '%s\n%s\n%s\n%s\n%s' "$ts" "$nonce" "$method" "$path" "$hash")
sig="v1=$(printf '%s' "$canonical" | openssl dgst -sha256 -hmac "$secret" -hex | awk '{print $NF}')"
args=(-sS -X "$method" "$base$path" -H "X-Solvelt-Timestamp: $ts" -H "X-Solvelt-Nonce: $nonce" -H "X-Solvelt-Signature: $sig")
if [ -n "$file" ]; then args+=(-H "Content-Type: application/json" --data-binary "@$file"); fi
# shellcheck disable=SC2086
curl ${CURL_OPTS:-} "${args[@]}"
