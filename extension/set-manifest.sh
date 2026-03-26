#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <prod|staging>"
  exit 1
fi

ENVIRONMENT="$1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_MANIFEST="${SCRIPT_DIR}/manifest.json"

case "${ENVIRONMENT}" in
  prod)
    SOURCE_MANIFEST="${SCRIPT_DIR}/manifest.prod.json"
    ;;
  staging)
    SOURCE_MANIFEST="${SCRIPT_DIR}/manifest.staging.json"
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

cp "${SOURCE_MANIFEST}" "${TARGET_MANIFEST}"
echo "Updated manifest.json to ${ENVIRONMENT} mode."
