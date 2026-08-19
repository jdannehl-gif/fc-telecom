#!/usr/bin/env bash
#
# Generate the migration SQL and check it for the four failure modes docs/11 §3 step 3 lists
# as most likely. Read-only: it never touches a database.
#
#   ./scripts/validate/02-review-migration.sh
#
# Why this exists: "review the generated migration before applying it" is correct advice and
# almost impossible to follow well on a 54-entity schema. The four checks below are the ones
# where a human skimming several thousand lines of DDL reliably misses the problem, and where
# the consequence is either a failed apply or — worse — a successful apply of the wrong thing.
#
set -uo pipefail

INFRA_PROJECT="src/FcTelecom.Infrastructure"
STARTUP_PROJECT="src/FcTelecom.Web"
OUT_DIR="artifacts/validation"
SQL_FILE="${OUT_DIR}/migration.sql"

mkdir -p "$OUT_DIR"

if ! dotnet ef --version >/dev/null 2>&1; then
  echo "dotnet-ef not installed. Run:" >&2
  echo "  dotnet tool install --global dotnet-ef" >&2
  exit 2
fi

echo "Generating idempotent migration script..."
if ! dotnet ef migrations script --idempotent \
      --project "$INFRA_PROJECT" \
      --startup-project "$STARTUP_PROJECT" \
      --output "$SQL_FILE" \
      --no-build 2>"${OUT_DIR}/ef-errors.txt"; then
  echo
  echo "Script generation failed. This is itself a finding — it is the first time the EF" >&2
  echo "model is actually validated, and model errors surface here rather than at runtime." >&2
  echo >&2
  cat "${OUT_DIR}/ef-errors.txt" >&2
  exit 1
fi

echo "Wrote ${SQL_FILE} ($(wc -l < "$SQL_FILE") lines)"
echo

python3 - "$SQL_FILE" <<'PY'
import collections
import re
import sys

sql = open(sys.argv[1], encoding="utf-8-sig").read()
findings = 0


def head(title):
    print(f"\n\033[1m{title}\033[0m")


def ok(msg):
    print(f"  \033[32mok\033[0m    {msg}")


def bad(msg):
    global findings
    findings += 1
    print(f"  \033[31mFLAG\033[0m  {msg}")


def note(msg):
    print(f"        {msg}")


# ── 1. Multiple cascade paths ───────────────────────────────────────────────────────────
#
# SQL Server rejects more than one cascade path into a table. EF will happily generate it and
# the apply fails with error 1785, which names the constraint but not the other path.
head("1. Cascade paths")

cascades = collections.defaultdict(list)
for match in re.finditer(
        r'ALTER TABLE \[(\w+)\]\s+ADD CONSTRAINT \[(\w+)\] FOREIGN KEY.*?'
        r'REFERENCES \[(\w+)\].*?ON DELETE CASCADE',
        sql, re.DOTALL | re.IGNORECASE):
    child, constraint, parent = match.groups()
    cascades[parent].append((child, constraint))

total = sum(len(v) for v in cascades.values())
if total == 0:
    ok("no ON DELETE CASCADE constraints at all")
else:
    ok(f"{total} cascading foreign key(s) across {len(cascades)} parent table(s)")
    for parent, children in sorted(cascades.items()):
        if len(children) > 1:
            bad(f"[{parent}] is the target of {len(children)} cascading FKs:")
            for child, constraint in children:
                note(f"from [{child}] via {constraint}")
            note("Two cascade paths into one table is SQL Server error 1785. Set the LESS")
            note("important side to NoAction — never both to Cascade.")

# ── 2. Filtered index columns ───────────────────────────────────────────────────────────
#
# Several indexes filter on [IsArchived] = 0. If a column was renamed, the filter references a
# column that does not exist and SQL Server rejects the index — but only for that one index,
# so a partially-applied migration is a real possibility.
head("2. Filtered index predicates")

table_columns = {}
for match in re.finditer(r'CREATE TABLE \[(\w+)\] \((.*?)\n\);', sql, re.DOTALL):
    table, body = match.groups()
    table_columns[table] = set(re.findall(r'\[(\w+)\]\s+\w', body))

filtered = re.findall(
    r'CREATE (?:UNIQUE )?INDEX \[(\w+)\]\s+ON \[(\w+)\] \([^)]*\)\s+WHERE (.+?);',
    sql, re.IGNORECASE)

if not filtered:
    ok("no filtered indexes")
else:
    ok(f"{len(filtered)} filtered index(es)")
    for index_name, table, predicate in filtered:
        referenced = set(re.findall(r'\[(\w+)\]', predicate))
        known = table_columns.get(table)
        if known is None:
            note(f"{index_name}: table [{table}] not created in this script (pre-existing)")
            continue
        missing = referenced - known
        if missing:
            bad(f"{index_name} on [{table}] filters on column(s) that table does not have: "
                f"{', '.join(sorted(missing))}")
            note(f"predicate: WHERE {predicate.strip()}")

# ── 3. rowversion, not varbinary ────────────────────────────────────────────────────────
#
# The concurrency token only works if the column is a real rowversion. As varbinary it is a
# column nobody writes and nobody checks, and concurrent edits silently overwrite each other.
head("3. RowVersion column type")

rowversion_cols = re.findall(r'\[RowVersion\]\s+(\w+(?:\(\w+\))?)', sql, re.IGNORECASE)
if not rowversion_cols:
    bad("no RowVersion column found in the script at all")
    note("Every BaseEntity-derived table should have one. Check ApplyRowVersionConvention.")
else:
    wrong = [t for t in rowversion_cols if t.lower() != "rowversion"]
    if wrong:
        bad(f"{len(wrong)} of {len(rowversion_cols)} RowVersion columns are not 'rowversion': "
            f"{', '.join(sorted(set(wrong)))}")
        note("As varbinary the column exists but is never populated or compared, so optimistic")
        note("concurrency silently does nothing. This is the lost-update defect, restored.")
    else:
        ok(f"all {len(rowversion_cols)} RowVersion columns are 'rowversion'")

# ── 4. Optional owned type nullability ──────────────────────────────────────────────────
#
# Location.MailingAddress is an optional owned type whose CLR properties are non-nullable. EF
# should emit nullable columns and use a required property for existence detection. If it emits
# NOT NULL, every location without a mailing address becomes unsavable.
head("4. Optional owned type: Location.MailingAddress")

location = re.search(r'CREATE TABLE \[Locations\] \((.*?)\n\);', sql, re.DOTALL)
if not location:
    note("Locations table not created in this script — skipping")
else:
    owned = re.findall(r'\[(MailingAddress_\w+)\]\s+([\w()]+)\s+(NOT NULL|NULL)', location.group(1))
    if not owned:
        note("no MailingAddress_* columns found; the owned type may be named differently")
    else:
        not_null = [c for c, _, n in owned if n == "NOT NULL"]
        if not_null:
            bad(f"{len(not_null)} MailingAddress column(s) are NOT NULL: {', '.join(not_null)}")
            note("An optional owned type with NOT NULL columns cannot be absent. Any location")
            note("without a mailing address will fail to save.")
        else:
            ok(f"all {len(owned)} MailingAddress columns are nullable")

# ── Result ──────────────────────────────────────────────────────────────────────────────
print()
if findings == 0:
    print("\033[32mNo findings from the four automated checks.\033[0m")
    print()
    print("These are heuristics over generated SQL, not a substitute for reading it. Still")
    print("scan the script for: unexpected table drops, NVARCHAR(MAX) where a length was")
    print("intended, and check constraints that did not make it across.")
    sys.exit(0)

print(f"\033[31m{findings} finding(s).\033[0m Fix the model and regenerate — do not hand-edit")
print("the migration. A hand-edited migration and the model it came from disagree forever.")
sys.exit(1)
PY
