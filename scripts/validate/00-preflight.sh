#!/usr/bin/env bash
#
# Preflight for the Azure validation pass. Run this before anything is deployed.
#
# Everything here is cheap and read-only. The point is that each check below has a failure
# mode that is expensive or confusing to diagnose *after* a deployment, and trivial to catch
# before one.
#
#   ./scripts/validate/00-preflight.sh dev
#   ./scripts/validate/00-preflight.sh prod
#
set -uo pipefail

ENVIRONMENT="${1:-dev}"
PARAM_FILE="infra/main.${ENVIRONMENT}.bicepparam"
FAILURES=0

pass() { printf '  \033[32mok\033[0m    %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; FAILURES=$((FAILURES + 1)); }
warn() { printf '  \033[33mwarn\033[0m  %s\n' "$1"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$1"; }

echo "Preflight for environment: ${ENVIRONMENT}"

# ── Tooling ─────────────────────────────────────────────────────────────────────────────
head_ "Tooling"

for tool in az dotnet; do
  if command -v "$tool" >/dev/null 2>&1; then
    pass "$tool on PATH"
  else
    fail "$tool not on PATH"
  fi
done

if az bicep version >/dev/null 2>&1; then
  pass "bicep available ($(az bicep version 2>&1 | head -1))"
else
  fail "bicep not installed — run: az bicep install"
fi

# ── Subscription ────────────────────────────────────────────────────────────────────────
head_ "Subscription"

if ACCOUNT=$(az account show -o json 2>/dev/null); then
  SUB_NAME=$(echo "$ACCOUNT" | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')
  SUB_ID=$(echo "$ACCOUNT" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
  TENANT=$(echo "$ACCOUNT" | python3 -c 'import json,sys; print(json.load(sys.stdin)["tenantId"])')
  pass "signed in to '${SUB_NAME}' (${SUB_ID})"
  pass "tenant ${TENANT}"
  echo
  warn "CONFIRM this is the intended subscription before continuing. Deploying dev"
  warn "infrastructure into a production subscription is the expensive kind of mistake."
else
  fail "not signed in — run: az login"
fi

# ── Resource providers ──────────────────────────────────────────────────────────────────
#
# An unregistered provider fails the deployment several minutes in, with an error that names
# the provider but not the fact that registration is a one-time subscription-level action.
head_ "Resource providers"

for provider in Microsoft.Sql Microsoft.Web Microsoft.KeyVault Microsoft.Storage \
                Microsoft.Insights Microsoft.OperationalInsights; do
  STATE=$(az provider show -n "$provider" --query registrationState -o tsv 2>/dev/null || echo "unknown")
  if [ "$STATE" = "Registered" ]; then
    pass "$provider registered"
  else
    fail "$provider is '${STATE}' — run: az provider register -n $provider"
  fi
done

# ── Parameter file ──────────────────────────────────────────────────────────────────────
#
# This is the check worth having. The committed dev parameter file ships with placeholder
# all-zero object IDs, because real tenant identifiers do not belong in source control. Deploy
# with them in place and the Key Vault and SQL admin role assignments are made against a
# principal that does not exist: the deployment may well succeed, and then nobody can read the
# vault and nobody can administer the database. That is a genuinely awful thing to debug.
head_ "Parameter file: ${PARAM_FILE}"

if [ ! -f "$PARAM_FILE" ]; then
  fail "not found"
else
  pass "exists"

  PLACEHOLDER='00000000-0000-0000-0000-000000000000'
  GUID_RE='[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

  while IFS= read -r line; do
    NAME=$(echo "$line" | sed -E "s/^param ([A-Za-z]+) *=.*/\1/")
    VALUE=$(echo "$line" | sed -E "s/.*'([^']*)'.*/\1/")

    if [ "$VALUE" = "$PLACEHOLDER" ]; then
      fail "$NAME is still the placeholder all-zero GUID"
    elif ! echo "$VALUE" | grep -Eq "^${GUID_RE}$"; then
      fail "$NAME is not a GUID ('${VALUE}') — object IDs only, never display names"
    else
      # Resolve it. A well-formed GUID that names nothing is the same failure, later.
      if DISPLAY=$(az ad group show --group "$VALUE" --query displayName -o tsv 2>/dev/null); then
        pass "$NAME resolves to Entra group '${DISPLAY}'"
      else
        fail "$NAME is a valid GUID but does not resolve to a group in this tenant"
      fi
    fi
  done < <(grep -E "^param .*ObjectId *=" "$PARAM_FILE")
fi

# ── Bicep compiles ──────────────────────────────────────────────────────────────────────
head_ "Bicep"

if az bicep build --file infra/main.bicep --stdout >/dev/null 2>&1; then
  pass "infra/main.bicep compiles"
else
  fail "infra/main.bicep does not compile — run: az bicep build --file infra/main.bicep --stdout"
fi

# ── Result ──────────────────────────────────────────────────────────────────────────────
echo
if [ "$FAILURES" -eq 0 ]; then
  printf '\033[32mPreflight clean.\033[0m Next: ./scripts/validate/01-infra-whatif.sh %s <resource-group>\n' "$ENVIRONMENT"
  exit 0
fi

printf '\033[31m%d preflight failure(s).\033[0m Fix these before deploying — every one of them\n' "$FAILURES"
printf 'is cheaper to fix now than to diagnose from a half-deployed environment.\n'
exit 1
