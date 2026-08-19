#!/usr/bin/env bash
#
# Post-deployment smoke test against a running instance.
#
#   ./scripts/validate/03-smoke.sh https://fctelecom-dev-web.azurewebsites.net
#
# Covers the parts of docs/11 §4.3 and §4.5 that a machine can check unauthenticated. It
# deliberately does NOT cover authorization — §4.4 needs real accounts in each role, one at a
# time, and it is the most valuable hour in the plan precisely because it cannot be scripted.
#
set -uo pipefail

BASE_URL="${1:-}"
if [ -z "$BASE_URL" ]; then
  echo "usage: $0 <base-url>" >&2
  exit 2
fi
BASE_URL="${BASE_URL%/}"

FAILURES=0
pass() { printf '  \033[32mok\033[0m    %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; FAILURES=$((FAILURES + 1)); }
warn() { printf '  \033[33mwarn\033[0m  %s\n' "$1"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$1"; }

echo "Smoke test: ${BASE_URL}"

# ── Health ──────────────────────────────────────────────────────────────────────────────
head_ "Health endpoints"

LIVE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "${BASE_URL}/health/live" || echo "000")
if [ "$LIVE" = "200" ]; then
  pass "/health/live returns 200"
else
  fail "/health/live returned ${LIVE}"
fi

READY_BODY=$(curl -s --max-time 60 "${BASE_URL}/health/ready" || echo "")
READY=$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 "${BASE_URL}/health/ready" || echo "000")
if [ "$READY" = "200" ]; then
  pass "/health/ready returns 200 (sql, outbox, probes all healthy)"
else
  fail "/health/ready returned ${READY}"
  [ -n "$READY_BODY" ] && printf '        %s\n' "$READY_BODY"
  warn "This is the first real test of the managed-identity SQL connection."
  warn "A failure here is usually the app's identity missing a database user, not the app."
fi

# ── Transport and headers ───────────────────────────────────────────────────────────────
head_ "Transport and security headers"

HEADERS=$(curl -s -D - -o /dev/null --max-time 30 "${BASE_URL}/" || echo "")

check_header() {
  local name="$1" expected="$2"
  local value
  value=$(echo "$HEADERS" | grep -i "^${name}:" | head -1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//')
  if [ -z "$value" ]; then
    fail "${name} missing"
  elif [ -n "$expected" ] && ! echo "$value" | grep -qi "$expected"; then
    fail "${name}: '${value}' (expected to contain '${expected}')"
  else
    pass "${name}: ${value}"
  fi
}

check_header "Strict-Transport-Security" "max-age"
check_header "X-Content-Type-Options"    "nosniff"
check_header "X-Frame-Options"           "DENY"
check_header "Referrer-Policy"           ""
check_header "Content-Security-Policy"   "default-src 'self'"

for banner in Server X-Powered-By X-AspNet-Version; do
  if echo "$HEADERS" | grep -qi "^${banner}:"; then
    fail "${banner} header is present — it should have been stripped"
  else
    pass "${banner} not disclosed"
  fi
done

# The CSP is the one worth reading rather than just matching. Blazor Server needs
# wasm-unsafe-eval; it does not need blanket unsafe-inline, and most Blazor CSP examples reach
# for it, which gives away most of the protection.
CSP=$(echo "$HEADERS" | grep -i '^content-security-policy:' | tr -d '\r' | head -1)
if echo "$CSP" | grep -q "unsafe-inline"; then
  fail "CSP contains 'unsafe-inline' — that is most of the protection gone"
else
  pass "CSP has no 'unsafe-inline'"
fi

# ── HTTP redirect ───────────────────────────────────────────────────────────────────────
head_ "HTTPS redirect"

HTTP_URL="http://${BASE_URL#https://}"
REDIRECT=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "$HTTP_URL" || echo "000")
if [ "$REDIRECT" = "301" ] || [ "$REDIRECT" = "302" ] || [ "$REDIRECT" = "307" ] || [ "$REDIRECT" = "308" ]; then
  pass "plain HTTP redirects (${REDIRECT})"
else
  warn "plain HTTP returned ${REDIRECT} — App Service may be terminating this before the app"
fi

# ── Anonymous access boundary ───────────────────────────────────────────────────────────
#
# Every page carries an explicit authorization policy. Anonymous should be redirected to
# sign-in, never served content. A 200 with a page body here is the serious finding.
head_ "Anonymous access"

for path in / /locations /services /vendors; do
  CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "${BASE_URL}${path}" || echo "000")
  if [ "$CODE" = "302" ] || [ "$CODE" = "401" ]; then
    pass "${path} → ${CODE} (redirected to sign-in)"
  elif [ "$CODE" = "200" ]; then
    fail "${path} returned 200 to an anonymous caller — check the page's [Authorize] attribute"
  else
    warn "${path} returned ${CODE}"
  fi
done

# ── Result ──────────────────────────────────────────────────────────────────────────────
echo
if [ "$FAILURES" -eq 0 ]; then
  printf '\033[32mSmoke test clean.\033[0m\n'
else
  printf '\033[31m%d failure(s).\033[0m\n' "$FAILURES"
fi

cat <<'EOF'

Not covered here, and not skippable:

  §4.3  Attempt an UPDATE on dbo.AuditEntries as the app identity and confirm it is denied.
        Confirm the reporting principal can read rpt.* and CANNOT read dbo.*.
        Upload a document and confirm the download URL expires.
        Confirm a seeded IP assignment decrypts — the Key Vault path has never had a key.

  §4.4  Sign in as one real account per role and check what each can and cannot see. This is
        the most valuable hour in the plan and there is no way to script it: the failure you
        are looking for is a role seeing something it should not, which requires a human who
        knows what that role is for.

  §4.5  Emit a log event containing a sensitive property and confirm it arrives REDACTED in
        Application Insights. Test this explicitly. The destructuring policy has never run.
EOF

exit "$FAILURES"
