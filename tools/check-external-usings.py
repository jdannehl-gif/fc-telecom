#!/usr/bin/env python3
"""Catch a well-known external symbol used without its namespace in scope.

Written after a CI run produced 203 CS0246 errors from one missing line: every file in
FcTelecom.Domain.UnitTests imported Shouldly and forgot Xunit, so [Fact], [Theory] and
[InlineData] resolved to nothing. ImplicitUsings is on but the SDK set does not include Xunit.

Run it before pushing:
    python3 tools/check-external-usings.py

This checks a table of well-known external symbols against the usings actually in scope for
each file: file-level `using` directives, `<Using Include="..." />` items in the owning
.csproj, and the SDK's implicit-usings set.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Implicit usings enabled by the .NET SDK when ImplicitUsings=enable.
SDK_IMPLICIT = {
    "System", "System.Collections.Generic", "System.IO", "System.Linq",
    "System.Net.Http", "System.Threading", "System.Threading.Tasks",
}
WEB_SDK_IMPLICIT = SDK_IMPLICIT | {
    "System.Net.Http.Json", "Microsoft.AspNetCore.Builder",
    "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http",
    "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration",
    "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting",
    "Microsoft.Extensions.Logging",
}

# symbol -> namespace that must be in scope. Attributes are matched as [Name] or [Name(...)].
ATTRIBUTES = {
    "Fact": "Xunit", "Theory": "Xunit", "InlineData": "Xunit", "MemberData": "Xunit",
    "ClassData": "Xunit", "Trait": "Xunit", "Collection": "Xunit",
}
TYPES = {
    "Assert": "Xunit",
    "Types": "NetArchTest.Rules",
    "DbContext": "Microsoft.EntityFrameworkCore",
    "DbSet": "Microsoft.EntityFrameworkCore",
    "IEntityTypeConfiguration": "Microsoft.EntityFrameworkCore",
    "EntityTypeBuilder": "Microsoft.EntityFrameworkCore.Metadata.Builders",
    "CultureInfo": "System.Globalization",
    "NumberStyles": "System.Globalization",
    "JsonSerializer": "System.Text.Json",
    "Regex": "System.Text.RegularExpressions",
    "IPAddress": "System.Net",
    "ClaimsPrincipal": "System.Security.Claims",
    "ClaimsIdentity": "System.Security.Claims",
    "Claim": "System.Security.Claims",
}
# Extension methods: their namespace must be imported for the call to bind.
EXTENSIONS = {
    "ShouldBe": "Shouldly", "ShouldNotBe": "Shouldly", "ShouldBeNull": "Shouldly",
    "ShouldNotBeNull": "Shouldly", "ShouldBeTrue": "Shouldly", "ShouldBeFalse": "Shouldly",
    "ShouldBeEmpty": "Shouldly", "ShouldNotBeEmpty": "Shouldly", "ShouldContain": "Shouldly",
    "ToListAsync": "Microsoft.EntityFrameworkCore", "AnyAsync": "Microsoft.EntityFrameworkCore",
    "FirstOrDefaultAsync": "Microsoft.EntityFrameworkCore", "CountAsync": "Microsoft.EntityFrameworkCore",
    "SingleOrDefaultAsync": "Microsoft.EntityFrameworkCore", "ToArrayAsync": "Microsoft.EntityFrameworkCore",
}


def project_usings(csproj: Path) -> set[str]:
    text = csproj.read_text(encoding="utf-8")
    declared = set(re.findall(r'<Using\s+Include="([^"]+)"', text))
    sdk = re.search(r'<Project\s+Sdk="([^"]+)"', text)
    implicit = WEB_SDK_IMPLICIT if sdk and "Web" in sdk.group(1) else SDK_IMPLICIT
    return declared | implicit


def owning_project(path: Path) -> Path | None:
    for parent in path.parents:
        found = list(parent.glob("*.csproj"))
        if found:
            return found[0]
        if parent == ROOT:
            return None
    return None


findings = []
project_cache: dict[Path, set[str]] = {}

for path in sorted(list(ROOT.rglob("*.cs")) + list(ROOT.rglob("*.razor"))):
    rel = path.relative_to(ROOT)
    if "obj/" in str(rel) or "bin/" in str(rel):
        continue

    csproj = owning_project(path)
    if csproj is None:
        continue
    if csproj not in project_cache:
        project_cache[csproj] = project_usings(csproj)

    raw = path.read_text(encoding="utf-8")

    # Strip comments and string literals before looking for symbols. Without this, a doc
    # comment mentioning <c>DbContext</c> reads as a use of DbContext. Usings are read from
    # the raw text, since stripping happens after.
    text = re.sub(r'/\*.*?\*/', '', raw, flags=re.DOTALL)
    text = re.sub(r'^\s*///.*$', '', text, flags=re.MULTILINE)
    text = re.sub(r'(?<!:)//.*$', '', text, flags=re.MULTILINE)
    text = re.sub(r'"(?:[^"\\\n]|\\.)*"', '""', text)

    in_scope = set(project_cache[csproj])
    in_scope |= set(re.findall(r'^\s*(?:@)?using\s+(?:static\s+)?([\w\.]+)\s*;', raw, re.MULTILINE))
    # A file in namespace X.Y can see types in X.Y and its ancestors.
    ns = re.search(r'^\s*namespace\s+([\w\.]+)', raw, re.MULTILINE)
    if ns:
        parts = ns.group(1).split(".")
        in_scope |= {".".join(parts[:i]) for i in range(1, len(parts) + 1)}

    missing: dict[str, str] = {}

    for symbol, namespace in ATTRIBUTES.items():
        if re.search(r'^\s*\[' + symbol + r'[\]\(]', text, re.MULTILINE) and namespace not in in_scope:
            missing[symbol] = namespace
    for symbol, namespace in TYPES.items():
        if re.search(r'(?<![\w.])' + symbol + r'\s*[<\.\(]', text) and namespace not in in_scope:
            missing[symbol] = namespace
    for symbol, namespace in EXTENSIONS.items():
        if re.search(r'\.' + symbol + r'\s*[\(<]', text) and namespace not in in_scope:
            missing[symbol] = namespace

    for symbol, namespace in sorted(missing.items()):
        findings.append(f"  {rel}: '{symbol}' used but '{namespace}' is not in scope")

if findings:
    print(f"CS0246 RISK — {len(findings)} finding(s):\n")
    print("\n".join(findings))
else:
    print("Every well-known external symbol has its namespace in scope.")

sys.exit(1 if findings else 0)
