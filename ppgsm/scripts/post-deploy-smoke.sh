#!/usr/bin/env bash
set -euo pipefail

: "${PPGSM_API_URL:?Set PPGSM_API_URL to the deployed API URL}"
: "${PPGSM_SMOKE_PRIMARY_TOKEN:?Supply a JWT for the primary smoke-test identity}"
: "${PPGSM_SMOKE_SECONDARY_TOKEN:?Supply a JWT for an identity without access to the primary test tenant}"
: "${PPGSM_SMOKE_CUSTOMER_ID:?Supply the isolated non-customer test CustomerId}"
: "${PPGSM_SMOKE_SNAPSHOT_ID:?Supply a durable test SnapshotId}"
: "${PPGSM_SMOKE_EVIDENCE_ID:?Supply an evidence ID owned by the primary test tenant}"
: "${PPGSM_SMOKE_REVOCATION_URL:?Supply an authenticated revocation verification endpoint}"
: "${PPGSM_SMOKE_DELETION_URL:?Supply an authenticated deletion verification endpoint}"

results_dir="${PPGSM_SMOKE_RESULTS_DIR:-deployment-evidence/smoke}"
mkdir -p "$results_dir"
results_file="$results_dir/contracts.tsv"
printf 'contract\tresult\tdetail\n' > "$results_file"

record() {
  printf '%s\t%s\t%s\n' "$1" "$2" "$3" | tee -a "$results_file"
}

expect_status() {
  local contract="$1"
  local expected="$2"
  local token="$3"
  local method="$4"
  local url="$5"
  local body="${6:-}"
  local output="$results_dir/${contract}.json"
  local args=(--silent --show-error --output "$output" --write-out '%{http_code}' --request "$method")
  if [[ -n "$token" ]]; then
    args+=(--header "Authorization: Bearer ${token}")
  fi
  if [[ -n "$body" ]]; then
    args+=(--header 'Content-Type: application/json' --data "$body")
  fi
  local status
  status=$(curl "${args[@]}" "$url")
  if [[ "$status" != "$expected" ]]; then
    record "$contract" FAIL "expected HTTP ${expected}, received ${status}"
    return 1
  fi
  record "$contract" PASS "HTTP ${status}"
}

base="${PPGSM_API_URL%/}/api/v1"
expect_status jwt_missing 401 '' GET "$base/capabilities"
expect_status jwt_invalid 401 'not-a-jwt' GET "$base/capabilities"
expect_status jwt_valid 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/capabilities"
expect_status sql_persistence 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/snapshots/$PPGSM_SMOKE_SNAPSHOT_ID"
expect_status rls_negative 403 "$PPGSM_SMOKE_SECONDARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/snapshots/$PPGSM_SMOKE_SNAPSHOT_ID"
expect_status blob_owner_read 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/snapshots/$PPGSM_SMOKE_SNAPSHOT_ID/evidence/$PPGSM_SMOKE_EVIDENCE_ID"
expect_status blob_non_owner_read 403 "$PPGSM_SMOKE_SECONDARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/snapshots/$PPGSM_SMOKE_SNAPSHOT_ID/evidence/$PPGSM_SMOKE_EVIDENCE_ID"
expect_status evidence_list 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/snapshots/$PPGSM_SMOKE_SNAPSHOT_ID/evidence?page=1&pageSize=20"
expect_status evidence_list_cross_tenant 403 "$PPGSM_SMOKE_SECONDARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/snapshots/$PPGSM_SMOKE_SNAPSHOT_ID/evidence?page=1&pageSize=20"
expect_status consent_metadata 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/consent-metadata"
expect_status consent_metadata_cross_tenant 403 "$PPGSM_SMOKE_SECONDARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/consent-metadata"
expect_status queue_sql_ownership 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/snapshots/$PPGSM_SMOKE_SNAPSHOT_ID"
expect_status export_create 202 "$PPGSM_SMOKE_PRIMARY_TOKEN" POST "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/exports" '{"format":"Json"}'
export_id=$(jq -er '.exportJobId' "$results_dir/export_create.json")
for attempt in $(seq 1 90); do
  expect_status export_status 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/exports/$export_id"
  export_status=$(jq -er '.status' "$results_dir/export_status.json")
  if [[ "$export_status" == "Completed" ]]; then break; fi
  if [[ "$export_status" == "Failed" ]]; then
    record export_complete FAIL "worker reported Failed"
    exit 1
  fi
  if [[ "$attempt" == "90" ]]; then
    record export_complete FAIL "export did not complete within fifteen minutes"
    exit 1
  fi
  sleep 10
done
record export_complete PASS "export reached Completed"
expect_status export_download 302 "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/exports/$export_id/download"
expect_status export_download_url 200 "$PPGSM_SMOKE_PRIMARY_TOKEN" POST "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/exports/$export_id/download-url"
expect_status export_download_cross_tenant 403 "$PPGSM_SMOKE_SECONDARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/exports/$export_id/download"
expect_status export_download_url_cross_tenant 403 "$PPGSM_SMOKE_SECONDARY_TOKEN" POST "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/exports/$export_id/download-url"
expect_status deletion_certificate "${PPGSM_SMOKE_CERTIFICATE_EXPECTED_STATUS:-200}" "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/deletion/certificate"
expect_status deletion_certificate_cross_tenant 403 "$PPGSM_SMOKE_SECONDARY_TOKEN" GET "$base/customers/$PPGSM_SMOKE_CUSTOMER_ID/deletion/certificate"
expect_status audit_contract "${PPGSM_SMOKE_AUDIT_EXPECTED_STATUS:-200}" "$PPGSM_SMOKE_PRIMARY_TOKEN" GET "${PPGSM_SMOKE_AUDIT_URL:?Supply an authenticated audit verification endpoint}"
expect_status revocation_contract "${PPGSM_SMOKE_REVOCATION_EXPECTED_STATUS:-200}" "$PPGSM_SMOKE_PRIMARY_TOKEN" POST "$PPGSM_SMOKE_REVOCATION_URL" "${PPGSM_SMOKE_REVOCATION_BODY:-{}}"
expect_status deletion_contract "${PPGSM_SMOKE_DELETION_EXPECTED_STATUS:-200}" "$PPGSM_SMOKE_PRIMARY_TOKEN" POST "$PPGSM_SMOKE_DELETION_URL" "${PPGSM_SMOKE_DELETION_BODY:-{}}"

record suite PASS 'all authenticated post-deploy contracts passed'
