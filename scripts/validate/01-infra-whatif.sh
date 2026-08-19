#!/usr/bin/env bash
#
# Run `az deployment group what-if` and FAIL if it proposes to delete or replace anything.
#
#   ./scripts/validate/01-infra-whatif.sh dev rg-fctelecom-dev
#
# docs/11 §4.1 says "what-if output reviewed, no unexpected Delete lines". A checklist item
# that depends on a person reading several hundred lines of diff carefully, every time, is a
# checklist item that will eventually be ticked without being done. This makes the machine
# read it.
#
# Delete and Deploy are the two that matter:
#   Delete  — the resource goes away. On a first deployment there should be none at all.
#   Deploy  — an existing resource is replaced rather than updated. On SQL or Storage that is
#             data loss, and it usually means an immutable property changed (a SKU family, a
#             location, an account kind).
#
set -uo pipefail

ENVIRONMENT="${1:-dev}"
RESOURCE_GROUP="${2:-}"

if [ -z "$RESOURCE_GROUP" ]; then
  echo "usage: $0 <environment> <resource-group>" >&2
  exit 2
fi

PARAM_FILE="infra/main.${ENVIRONMENT}.bicepparam"
OUT_DIR="artifacts/validation"
OUT_FILE="${OUT_DIR}/whatif-${ENVIRONMENT}.txt"

mkdir -p "$OUT_DIR"

echo "what-if: ${ENVIRONMENT} → ${RESOURCE_GROUP}"
echo

if ! az deployment group what-if \
      --resource-group "$RESOURCE_GROUP" \
      --template-file infra/main.bicep \
      --parameters "$PARAM_FILE" \
      --no-pretty-print > "$OUT_FILE" 2>&1; then
  echo "what-if itself failed. Output:" >&2
  cat "$OUT_FILE" >&2
  exit 1
fi

echo "Saved to ${OUT_FILE}"
echo

# --no-pretty-print gives JSON, which is worth parsing rather than grepping colourised text.
python3 - "$OUT_FILE" <<'PY'
import json, sys, collections

path = sys.argv[1]
try:
    with open(path) as handle:
        payload = json.load(handle)
except json.JSONDecodeError:
    print("Could not parse what-if output as JSON — review it by hand:", path)
    sys.exit(1)

changes = payload.get("changes", payload if isinstance(payload, list) else [])
counts = collections.Counter(change.get("changeType", "?") for change in changes)

print("Change summary")
for kind, count in sorted(counts.items()):
    print(f"  {kind:-12s} {count}")

destructive = [c for c in changes if c.get("changeType") in ("Delete", "Deploy")]

if not destructive:
    print("\n\033[32mNo Delete or Deploy (replace) operations.\033[0m")
    sys.exit(0)

print("\n\033[31mDestructive operations proposed:\033[0m\n")
for change in destructive:
    print(f"  {change.get('changeType')}: {change.get('resourceId', '?')}")
    for delta in change.get("delta", []) or []:
        before = delta.get("before")
        after = delta.get("after")
        print(f"      {delta.get('path')}: {before!r} -> {after!r}")

print("""
A 'Deploy' on an existing resource means REPLACE, not update — an immutable property changed.
On SQL or Storage that is data loss. Work out which property moved and why before proceeding;
if the change is genuinely intended, take a backup first and record the decision.
""")
sys.exit(1)
PY

STATUS=$?
if [ $STATUS -eq 0 ]; then
  echo
  echo "Next: deploy, then ./scripts/validate/02-review-migration.sh"
fi
exit $STATUS
