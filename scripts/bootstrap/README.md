# scripts/bootstrap

Provisioning for the **Ubuntu Server 26.04 LTS** host that runs the Azure validation pass.

```bash
sudo ./scripts/bootstrap/ubuntu-26.04.sh            # install
./scripts/bootstrap/ubuntu-26.04.sh --check         # report only; no root, no changes
./scripts/bootstrap/ubuntu-26.04.sh --help
```

Re-running is safe. Everything it installs is checked for first.

The application stays on **`net10.0`**. Nothing here changes the target framework, and this
host is a validation runner, not a build environment for a different one.

---

## Read this before the first run

There is exactly one decision you have to make: **how to install the Azure CLI.** Everything
else the script decides for you and explains as it goes.

```bash
sudo ./scripts/bootstrap/ubuntu-26.04.sh --azure-cli=container   # recommended; needs Docker
```

Why it is a decision at all: Microsoft's apt packages for the Azure CLI are tested on Ubuntu
22.04 and 24.04, and no `azure-cli` repository has been published for 26.04 (`resolute`).
Pointing a 26.04 host at the 24.04 (`noble`) repository would very likely work — and would be
an undeclared substitution that nobody remembers six months later when something breaks. So
`--azure-cli=auto` probes for a real 26.04 suite, and if there is none it stops and prints the
choices rather than silently taking one.

| `--azure-cli=` | What it does | Supported by Microsoft? |
|---|---|---|
| `auto` *(default)* | Use apt **if** a 26.04 suite exists, otherwise fail with an explanation | — |
| `container` | Writes an `az` wrapper around `mcr.microsoft.com/azure-cli:azurelinux3.0` | **Yes.** Official image, documented install method |
| `apt` | Forces the apt path; fails if there is no 26.04 suite | Yes, once published |
| `pipx` | Isolated venv from PyPI | **No.** The Azure CLI project documents pip for edge builds only |
| `skip` | Installs nothing; run the `az` steps elsewhere | — |

The container wrapper has real limitations, and it prints them into itself so they are on the
host rather than only in this file: only `$HOME` and the current working directory are visible
inside the container, it runs as your UID so files stay yours, and each invocation costs about
a second of container startup. Running the validation scripts from the repository root — which
the runbook already tells you to do — keeps all of that invisible.

To find out whether the apt situation has changed:

```bash
curl -fsI https://packages.microsoft.com/repos/azure-cli/dists/resolute/Release
```

A 200 means `--azure-cli=apt` will work. `.github/workflows/ubuntu-2604-compat.yml` runs that
same probe weekly and writes the answer into the run summary, so you do not have to remember to.

---

## What it installs, and why each source was chosen

| | Source | Why not the obvious alternative |
|---|---|---|
| **.NET 10 SDK** | Ubuntu archive (`dotnet-sdk-10.0`, in 26.04 main) | Microsoft's own guidance for Ubuntu 22.04+ is the native feed, and is explicit that mixing it with `packages.microsoft.com` "leads to problems when apps try to resolve a specific version of .NET". The script adds an APT pin so a Microsoft repository added later for some other reason cannot silently take over .NET. |
| **PowerShell 7.6** | GitHub release `.deb` | `apt install powershell` fails on 26.04: the Microsoft repository has a 26.04 bootstrap config, but the PowerShell packages themselves are not published there. Manual `.deb` install is a documented, supported method, and 26.04 is a supported PowerShell platform. |
| **Bicep** | Standalone binary from the Bicep release | `az bicep install` writes into the Azure CLI's own directory. Under `--azure-cli=container` that is a layer discarded on exit, so it would appear to succeed and then never be there. A host binary works under every strategy. `00-Preflight.ps1` prefers it and falls back to `az bicep`. |
| **`dotnet-ef` 10.x** | `dotnet tool install --global` | Installed as the invoking user, not root — global tools are per-user, and a `dotnet-ef` in `/root` that the operator cannot find is a genuinely confusing ten minutes. `~/.dotnet/tools` is added to `PATH`, because Ubuntu does not do that for you. |
| **SqlServer module** | PSGallery, `CurrentUser` scope | Verified by capability, not version: the script imports it and asserts `Invoke-Sqlcmd` exposes `-AccessToken`. That parameter is how `07-TestAppIdentity.ps1` connects as the App Service managed identity, and discovering its absence three steps into a pass — after a deployment — is the failure this check exists to prevent. |

`unixODBC` and `msodbcsql18` are **not** installed and **not** required. `Invoke-Sqlcmd` uses
the managed `Microsoft.Data.SqlClient` driver. `--with-odbc` installs unixODBC for anyone who
wants it for unrelated reasons, with a warning attached.

---

## What still cannot be supported on Ubuntu 26.04

Honest list. None of these is worked around with an unverified substitution.

### 1. `azure-cli` from apt — no 26.04 package

No `resolute` suite exists in `https://packages.microsoft.com/repos/azure-cli/`. Microsoft's
documented, tested apt platforms are Ubuntu 22.04 and 24.04.

- **Handled by:** `--azure-cli=container`, using the official Microsoft image. Supported
  software, no apt mixing.
- **Not done:** pointing 26.04 at the `noble` repository.
- **Resolves when:** the probe above returns 200; then `--azure-cli=apt` works with no other
  change, and `auto` picks it up by itself.

### 2. `powershell` from apt — no 26.04 package

The Microsoft repository has a bootstrap config for 26.04, so `apt install powershell` looks
like it should work and fails with "Unable to locate package". The packages are not published
there ([PowerShell/PowerShell-Docker#345](https://github.com/PowerShell/PowerShell-Docker/issues/345)).

- **Handled by:** installing the GitHub release `.deb`, which is a documented and supported
  installation method. 26.04 is a supported PowerShell platform; only the *feed* is missing.
- **Consequence:** PowerShell does not update via `apt upgrade`. Re-run the bootstrap script,
  or pass `--pwsh-version=X.Y.Z`.

### 3. `msodbcsql18` and the standalone `sqlcmd` — no 26.04 package

Microsoft's ODBC driver has no 26.04 package, so the classic `sqlcmd` cannot be installed the
usual way.

- **Impact on this pass: none.** Nothing in `scripts/validate/` shells out to `sqlcmd`. All SQL
  goes through `Invoke-Sqlcmd`, which uses the managed driver and needs no ODBC stack. That was
  a deliberate choice, and `00-Preflight.ps1` says so out loud so nobody installs an ODBC stack
  to satisfy a dependency this pass does not have.
- **If you want `sqlcmd` anyway:** the Go-based `sqlcmd` (`go-sqlcmd`) ships as a standalone
  binary with no ODBC dependency. Not installed by this script, because nothing needs it.

### 4. Interactive browser sign-in

A headless server has no browser. This is not a packaging gap — it is what a server is.

- **Handled by:** `az login --use-device-code` everywhere, in the scripts and both runbooks.
  `Test-LinuxCompatibility.ps1` fails the audit if any script tells an operator otherwise.

### 5. Admin-centre steps in `entra-setup-dev.md`

Creating groups, the app registration, granting consent and assigning groups are portal
operations. They cannot be performed on the validation host and are not scripted.

- **Handled by:** doing them from a browser on your workstation. `entra-setup-dev.md` now says
  which machine each part runs on.

### 6. `--azure-cli=container` needs Docker on the host

The container strategy is the recommended answer to item 1, but it requires a Docker daemon.
On a host where Docker is not permitted, the remaining options are `--azure-cli=pipx` (outside
Microsoft's supported path — opt in knowingly) or running the `az`-dependent validation steps
from another machine.

- **Not automated:** the script does not install Docker for you. Installing a container runtime
  is a host-policy decision, not a bootstrap detail.

### 7. Not verified: nothing here has been executed on a real 26.04 host by this repository

Everything above is from Microsoft's and Ubuntu's published package metadata and documentation.
`.github/workflows/ubuntu-2604-compat.yml` is what turns it into evidence: it runs the full
bootstrap and the non-authenticated preflight against a clean `ubuntu:26.04` image on every
change to these scripts, and weekly. **Read that workflow's most recent run before validation
day** — it is the difference between "this should work" and "this worked on Monday".

The one thing the workflow cannot cover is `--azure-cli=container`, because a container job has
no Docker daemon to nest one in. That path is exercised only on a real host.

---

## Ubuntu 24.04 and Windows

This script is 26.04-only and refuses to run elsewhere — its .NET source choice and its
PowerShell install method are both 26.04-specific, and applying them to 24.04 would replace
working apt packages with manually managed ones. `--force-os` overrides the guard for testing;
do not use it on a host you care about.

24.04 and Windows instructions are in `docs/runbooks/azure-validation.md` step 0. Both are
supported. 24.04 is simpler, because Microsoft publishes everything for `noble`.

---

## Verifying the result

```bash
pwsh ./scripts/validate/00-Preflight.ps1 -SkipAzureSignIn
```

Host, tooling versions, SQL client capability, parameter-file shape and Bicep compilation —
every check that needs no Azure credentials. Then, once signed in:

```bash
az login --use-device-code
az account set --subscription "<dev subscription>"
pwsh ./scripts/validate/00-Preflight.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

`docs/runbooks/azure-validation.md` is authoritative for everything after that.
