#!/usr/bin/env python3
"""Check every central package pin against what is actually published on nuget.org.

Why this exists
---------------
Three consecutive CI runs failed at restore on NU1109 "detected package downgrade". Each time
the cause was the same shape: a PackageVersion in Directory.Packages.props sitting below a
version something else in the dependency graph required. With
CentralPackageTransitivePinningEnabled=true, a central pin is a hard floor, and a floor that
is behind the published band is the single most likely thing to be underneath a transitive
requirement.

Learning that from a failed pipeline costs a full round trip. This script tells you before
you push.

What it does
------------
Reads Directory.Packages.props, asks the NuGet flat-container index what versions exist for
each package, and reports any pin that is not the newest stable release in its own
major.minor band. It deliberately does NOT flag a pin that is behind a newer *major* or
*minor* — those are decisions (Microsoft.Identity.Web 3.x vs 4.x is one), not drift.

It also re-checks rule 2: every PackageReference has a PackageVersion, and no PackageVersion
is declared that nothing references (with an allowlist for deliberate security pins).

Usage
-----
    python3 tools/check-package-pins.py            # report only
    python3 tools/check-package-pins.py --strict   # exit 1 on any finding

Requires network access to api.nuget.org. This is intentionally a local/pre-push tool rather
than a CI step: the pipeline should not gain a new external dependency while we are still
trying to make it deterministic.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MANIFEST = REPO_ROOT / "Directory.Packages.props"
FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer/{id}/index.json"

# Declared on purpose without a matching PackageReference. Each one needs a comment in the
# manifest saying what pulls it in and when the pin can be removed.
DELIBERATE_TRANSITIVE_PINS = {"System.Security.Cryptography.Xml"}

STABLE = re.compile(r"^(\d+)\.(\d+)\.(\d+)(\.\d+)?$")


def parse_manifest() -> dict[str, str]:
    text = MANIFEST.read_text(encoding="utf-8")
    return {
        m.group(1): m.group(2)
        for m in re.finditer(
            r'<PackageVersion\s+Include="([^"]+)"\s+Version="([^"]+)"', text
        )
    }


def parse_references() -> dict[str, list[str]]:
    refs: dict[str, list[str]] = {}
    for proj in REPO_ROOT.rglob("*.csproj"):
        for m in re.finditer(
            r'<PackageReference\s+Include="([^"]+)"([^/>]*)', proj.read_text(encoding="utf-8")
        ):
            refs.setdefault(m.group(1), []).append(str(proj.relative_to(REPO_ROOT)))
            if "Version=" in m.group(2):
                print(
                    f"  INLINE VERSION  {m.group(1)} in {proj.relative_to(REPO_ROOT)} — "
                    "versions belong in Directory.Packages.props only"
                )
    return refs


def published_versions(package_id: str) -> list[str] | None:
    url = FLAT_CONTAINER.format(id=package_id.lower())
    try:
        with urllib.request.urlopen(url, timeout=20) as response:
            return json.load(response).get("versions", [])
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        print(f"  LOOKUP FAILED   {package_id}: {exc}")
        return None


def as_tuple(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split(".") if part.isdigit())


def newest_in_band(versions: list[str], pinned: str) -> str | None:
    """Newest stable release sharing the pinned version's major.minor."""
    band = as_tuple(pinned)[:2]
    candidates = [
        v for v in versions if STABLE.match(v) and as_tuple(v)[:2] == band
    ]
    return max(candidates, key=as_tuple) if candidates else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--strict", action="store_true", help="exit 1 on any finding")
    args = parser.parse_args()

    print("Reading project references...")
    references = parse_references()
    declared = parse_manifest()

    findings = 0

    # Rule 1 — every reference has a version.
    missing = sorted(set(references) - set(declared))
    for package_id in missing:
        findings += 1
        print(f"  NU1010 RISK     {package_id} referenced by "
              f"{', '.join(references[package_id])} with no PackageVersion")

    # Rule 2 — nothing declared that nothing references.
    unreferenced = sorted(set(declared) - set(references) - DELIBERATE_TRANSITIVE_PINS)
    for package_id in unreferenced:
        findings += 1
        print(f"  NU1109 RISK     {package_id} {declared[package_id]} is declared but "
              "referenced by nothing — a stale floor under transitive pinning")

    # Rule 3 — pinned at the newest patch of its own band.
    print(f"\nChecking {len(declared)} pins against nuget.org...\n")
    for package_id, pinned in sorted(declared.items()):
        versions = published_versions(package_id)
        if versions is None:
            findings += 1
            continue

        if pinned not in versions:
            findings += 1
            print(f"  NOT PUBLISHED   {package_id} {pinned} does not exist on nuget.org")
            continue

        newest = newest_in_band(versions, pinned)
        if newest and as_tuple(newest) > as_tuple(pinned):
            findings += 1
            print(f"  BEHIND BAND     {package_id} {pinned} -> {newest} available")
        else:
            print(f"  ok              {package_id} {pinned}")

    print()
    if findings:
        print(f"{findings} finding(s). A 'BEHIND BAND' pin is the exact shape that produced "
              "NU1109 on CI runs 2 and 3 — move the whole band together.")
    else:
        print("All pins current within their bands, all references resolved, no stale floors.")

    return 1 if (findings and args.strict) else 0


if __name__ == "__main__":
    sys.exit(main())
