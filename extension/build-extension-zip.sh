#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <prod|staging>"
  exit 1
fi

ENVIRONMENT="$1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

case "${ENVIRONMENT}" in
  prod)
    SOURCE_MANIFEST="${SCRIPT_DIR}/manifest.prod.json"
    OUTPUT_ZIP="${REPO_ROOT}/extension.zip"
    ;;
  staging)
    SOURCE_MANIFEST="${SCRIPT_DIR}/manifest.staging.json"
    OUTPUT_ZIP="${REPO_ROOT}/extension-staging.zip"
    ;;
  *)
    echo "Invalid environment: ${ENVIRONMENT}"
    echo "Usage: $0 <prod|staging>"
    exit 1
    ;;
esac

if [[ ! -f "${SOURCE_MANIFEST}" ]]; then
  echo "Manifest file not found: ${SOURCE_MANIFEST}"
  exit 1
fi

TMP_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "${TMP_DIR}"
}
trap cleanup EXIT

cp -R "${SCRIPT_DIR}/." "${TMP_DIR}/"
cp "${SOURCE_MANIFEST}" "${TMP_DIR}/manifest.json"
rm -f "${TMP_DIR}/manifest.prod.json" "${TMP_DIR}/manifest.staging.json"

rm -f "${OUTPUT_ZIP}"
(cd "${TMP_DIR}" && zip -qr "${OUTPUT_ZIP}" .)

echo "Created ${OUTPUT_ZIP}"
