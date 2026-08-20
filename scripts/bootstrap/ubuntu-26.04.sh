#!/usr/bin/env bash
#
# Provision an Ubuntu Server 26.04 LTS host to run the Azure validation pass.
#
#   sudo ./scripts/bootstrap/ubuntu-26.04.sh
#   ./scripts/bootstrap/ubuntu-26.04.sh --check          # report only, mutate nothing
#   sudo ./scripts/bootstrap/ubuntu-26.04.sh --azure-cli=container
#
# ---------------------------------------------------------------------------------------
# WHAT THIS INSTALLS, AND WHY EACH CHOICE
#
#   .NET 10 SDK      From the UBUNTU ARCHIVE, not packages.microsoft.com. Microsoft's own
#                    guidance for Ubuntu 22.04+ is to use the native feed, and is explicit
#                    that mixing the two sources "leads to problems when apps try to resolve
#                    a specific version of .NET". dotnet-sdk-10.0 is in 26.04 main.
#
#   PowerShell 7.6   From the GitHub release .deb, NOT from apt. The Microsoft repository
#                    has a bootstrap config for 26.04 but the PowerShell packages themselves
#                    are not published there yet, so `apt install powershell` fails with
#                    "Unable to locate package". Manual .deb installation is a documented,
#                    supported method and 26.04 IS listed as a supported PowerShell platform.
#
#   Azure CLI        The hard one. See resolve_azure_cli_strategy() below. Microsoft's apt
#                    packages are tested on 22.04 and 24.04 only. This script will NOT point
#                    26.04 at the noble repository behind your back.
#
#   Bicep            Standalone binary from the Bicep GitHub release, with the Azure CLI
#                    configured to use it. This works no matter which Azure CLI strategy is
#                    chosen, including the container one where `az bicep install` would write
#                    into a layer that disappears.
#
# The application stays on net10.0. Nothing here changes its target framework, and this host
# is a validation runner, not a build environment for a different framework.
# ---------------------------------------------------------------------------------------

set -euo pipefail

readonly EXPECTED_VERSION_ID="26.04"
readonly PWSH_SERIES="7.6"
# Used only when the GitHub releases API is unreachable — a proxy, an egress rule, or the
# unauthenticated rate limit. Failing the whole bootstrap because a version-lookup call was
# throttled would be a poor trade; installing a known-good 7.6 and saying so is better.
readonly PWSH_FALLBACK_VERSION="7.6.0"
readonly DOTNET_SDK_PACKAGE="dotnet-sdk-10.0"
readonly DOTNET_EF_VERSION="10.*"

CHECK_ONLY=0
AZURE_CLI_STRATEGY="auto"
FORCE_OS=0
PWSH_VERSION=""
WITH_ODBC=0

FAILURES=0
WARNINGS=0

# ── Output ─────────────────────────────────────────────────────────────────────────────
if [ -t 1 ]; then
  C_RESET=$'\033[0m'; C_GREEN=$'\033[32m'; C_RED=$'\033[31m'
  C_YELLOW=$'\033[33m'; C_GREY=$'\033[90m'; C_BOLD=$'\033[1m'
else
  C_RESET=''; C_GREEN=''; C_RED=''; C_YELLOW=''; C_GREY=''; C_BOLD=''
fi

heading() { printf '\n%s%s%s\n' "$C_BOLD" "$1" "$C_RESET"; }
ok()      { printf '  %s[ ok ]%s %s\n' "$C_GREEN"  "$C_RESET" "$1"; }
fail()    { printf '  %s[FAIL]%s %s\n' "$C_RED"    "$C_RESET" "$1"; FAILURES=$((FAILURES + 1)); }
warn()    { printf '  %s[warn]%s %s\n' "$C_YELLOW" "$C_RESET" "$1"; WARNINGS=$((WARNINGS + 1)); }
note()    { printf '         %s%s%s\n' "$C_GREY" "$1" "$C_RESET"; }

usage() {
  cat <<'USAGE'
Usage: ubuntu-26.04.sh [options]

  --check                  Report what is present and what would be installed. Mutates
                           nothing and needs no root. Used by CI.
  --azure-cli=STRATEGY     auto (default) | apt | container | pipx | skip
                             auto       use apt if Microsoft has published a 26.04 package,
                                        otherwise FAIL with an explanation. Never falls back
                                        to the 24.04 repository.
                             apt        force the apt path; fails if no 26.04 package exists.
                             container  install an `az` wrapper around the official
                                        mcr.microsoft.com/azure-cli image. Requires Docker.
                             pipx       isolated venv from PyPI. NOT a Microsoft-supported
                                        install path for stable releases — opt in knowingly.
                             skip       do not install or check Azure CLI.
  --pwsh-version=X.Y.Z     Pin an exact PowerShell version instead of the latest 7.6.x.
  --with-odbc              Also install unixODBC. Not needed by this validation pass; see
                           the note in install_sql_tooling().
  --force-os               Run on a release other than 26.04. Unsupported; for testing only.
  -h, --help               This.
USAGE
}

for arg in "$@"; do
  case "$arg" in
    --check)            CHECK_ONLY=1 ;;
    --azure-cli=*)      AZURE_CLI_STRATEGY="${arg#*=}" ;;
    --pwsh-version=*)   PWSH_VERSION="${arg#*=}" ;;
    --with-odbc)        WITH_ODBC=1 ;;
    --force-os)         FORCE_OS=1 ;;
    -h|--help)          usage; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; usage; exit 2 ;;
  esac
done

case "$AZURE_CLI_STRATEGY" in
  auto|apt|container|pipx|skip) ;;
  *) echo "Invalid --azure-cli strategy: $AZURE_CLI_STRATEGY" >&2; exit 2 ;;
esac

run() {
  if [ "$CHECK_ONLY" -eq 1 ]; then
    note "would run: $*"
    return 0
  fi
  "$@"
}

require_root() {
  if [ "$CHECK_ONLY" -eq 1 ]; then return 0; fi
  if [ "$(id -u)" -ne 0 ]; then
    echo "This script needs root to install packages. Re-run with sudo, or use --check." >&2
    exit 1
  fi
}

# ── 1. Host ────────────────────────────────────────────────────────────────────────────
check_host() {
  heading "Host"

  if [ ! -r /etc/os-release ]; then
    fail "/etc/os-release not readable — cannot identify the distribution"
    return
  fi

  # shellcheck disable=SC1091
  . /etc/os-release

  note "${PRETTY_NAME:-unknown}"
  note "codename: ${VERSION_CODENAME:-unknown}   arch: $(dpkg --print-architecture 2>/dev/null || uname -m)"

  if [ "${ID:-}" != "ubuntu" ]; then
    fail "this script targets Ubuntu; found ID=${ID:-unknown}"
    return
  fi

  if [ "${VERSION_ID:-}" = "$EXPECTED_VERSION_ID" ]; then
    ok "Ubuntu $EXPECTED_VERSION_ID"
  elif [ "$FORCE_OS" -eq 1 ]; then
    warn "expected $EXPECTED_VERSION_ID, found ${VERSION_ID:-unknown} — continuing because --force-os"
    note "Package names and availability differ between releases. Expect breakage."
  else
    fail "expected Ubuntu $EXPECTED_VERSION_ID, found ${VERSION_ID:-unknown}"
    note "Use --force-os only if you understand what will differ."
    return
  fi

  local arch
  arch="$(dpkg --print-architecture 2>/dev/null || echo unknown)"
  if [ "$arch" != "amd64" ]; then
    warn "architecture is $arch, not amd64"
    note "The PowerShell .deb and Bicep binary URLs below assume x64. Adjust if on arm64."
  fi
}

# ── 2. Base utilities ──────────────────────────────────────────────────────────────────
install_base() {
  heading "Base utilities"

  local packages=(git curl wget ca-certificates apt-transport-https gnupg unzip jq)
  local missing=()

  for package in "${packages[@]}"; do
    if dpkg -s "$package" >/dev/null 2>&1; then
      ok "$package"
    else
      missing+=("$package")
    fi
  done

  if [ ${#missing[@]} -eq 0 ]; then return; fi

  note "missing: ${missing[*]}"
  run apt-get update -qq
  run apt-get install -y --no-install-recommends "${missing[@]}"

  if [ "$CHECK_ONLY" -eq 0 ]; then
    for package in "${missing[@]}"; do
      dpkg -s "$package" >/dev/null 2>&1 && ok "$package installed" || fail "$package failed to install"
    done
  fi
}

# ── 3. Keep .NET package sources unmixed ───────────────────────────────────────────────
#
# Two things here. First, we never add packages.microsoft.com for .NET — the Ubuntu archive
# has dotnet-sdk-10.0 and that is what Microsoft recommends on 22.04+. Second, we write an
# APT preference that deprioritises dotnet*/aspnet*/netstandard* from the Microsoft origin,
# so that if that repository is added later for some other product it cannot start serving
# .NET packages and quietly split the installation across two sources.
configure_dotnet_pinning() {
  heading ".NET package source"

  local pin_file="/etc/apt/preferences.d/99-dotnet-from-ubuntu.pref"

  if grep -rqs "packages.microsoft.com" /etc/apt/sources.list /etc/apt/sources.list.d/ 2>/dev/null; then
    warn "packages.microsoft.com is already configured as an APT source on this host"
    note "The pin below stops it serving .NET packages. Nothing else about it is changed."
  else
    ok "packages.microsoft.com is not an APT source (nothing to mix with)"
  fi

  if [ -f "$pin_file" ] && grep -q 'packages.microsoft.com' "$pin_file"; then
    ok "pin already present: $pin_file"
    return
  fi

  if [ "$CHECK_ONLY" -eq 1 ]; then
    note "would write $pin_file to deprioritise dotnet* from packages.microsoft.com"
    return
  fi

  cat > "$pin_file" <<'PIN'
# Source .NET from the Ubuntu archive, never from packages.microsoft.com.
#
# Microsoft's guidance for Ubuntu 22.04 and later is to use the native feed, and warns that
# mixing the two sources causes version-resolution failures and partial installations. This
# pin makes that decision durable: if packages.microsoft.com is added later for another
# product, it still cannot serve .NET.
#
# Written by scripts/bootstrap/ubuntu-26.04.sh
Package: dotnet* aspnet* netstandard*
Pin: origin "packages.microsoft.com"
Pin-Priority: -10
PIN

  ok "wrote $pin_file"
}

# ── 4. .NET 10 SDK ─────────────────────────────────────────────────────────────────────
install_dotnet() {
  heading ".NET 10 SDK (Ubuntu archive)"

  if command -v dotnet >/dev/null 2>&1; then
    local sdks
    sdks="$(dotnet --list-sdks 2>/dev/null || true)"
    if printf '%s' "$sdks" | grep -q '^10\.'; then
      ok "dotnet SDK $(dotnet --version 2>/dev/null) present"
    else
      warn "dotnet present but no 10.x SDK found"
      note "installed: $(printf '%s' "$sdks" | tr '\n' ' ')"
    fi
  fi

  if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    run apt-get update -qq
    run apt-get install -y "$DOTNET_SDK_PACKAGE"
  fi

  if [ "$CHECK_ONLY" -eq 1 ]; then return; fi

  if dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    ok "dotnet $(dotnet --version)"
  else
    fail "$DOTNET_SDK_PACKAGE did not produce a 10.x SDK"
    note "Check: apt-cache policy $DOTNET_SDK_PACKAGE"
    return
  fi

  # Confirm the package really came from Ubuntu. If this says packages.microsoft.com, the
  # pin was added too late and the install is already split across two sources.
  local origin
  origin="$(apt-cache policy "$DOTNET_SDK_PACKAGE" 2>/dev/null | grep -A1 '\*\*\*' | tail -1 | awk '{print $2}' || true)"
  if printf '%s' "$origin" | grep -q 'packages.microsoft.com'; then
    fail "$DOTNET_SDK_PACKAGE was installed from packages.microsoft.com, not the Ubuntu archive"
    note "Remove it, apply the pin, and reinstall:"
    note "  sudo apt-get remove --purge 'dotnet*' 'aspnetcore*' 'netstandard*'"
    note "  sudo apt-get install $DOTNET_SDK_PACKAGE"
  else
    ok "sourced from the Ubuntu archive (${origin:-local})"
  fi
}

# ── 5. PowerShell 7.6 LTS ──────────────────────────────────────────────────────────────
#
# Not from apt. packages.microsoft.com publishes a 26.04 bootstrap config, but the PowerShell
# binaries are not in that repository — `apt install powershell` returns "Unable to locate
# package". Microsoft documents manual .deb installation as a supported method, and lists
# 26.04 as a supported PowerShell platform, so that is the path taken here.
install_powershell() {
  heading "PowerShell $PWSH_SERIES LTS"

  if command -v pwsh >/dev/null 2>&1; then
    local current
    current="$(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || echo unknown)"
    case "$current" in
      "$PWSH_SERIES".*) ok "pwsh $current"; return ;;
      7.*)              warn "pwsh $current present, wanted $PWSH_SERIES.x"; note "Continuing; 7.x is sufficient for these scripts." ; return ;;
      *)                warn "pwsh $current present and is not 7.x" ;;
    esac
  fi

  local version="$PWSH_VERSION"
  if [ -z "$version" ]; then
    note "resolving the latest $PWSH_SERIES.x release from GitHub"
    if [ "$CHECK_ONLY" -eq 1 ] && ! command -v jq >/dev/null 2>&1; then
      note "would query the PowerShell releases API (jq not installed in check mode)"
      return
    fi
    version="$(curl -fsSL https://api.github.com/repos/PowerShell/PowerShell/releases \
      | jq -r '[.[] | select(.prerelease == false) | .tag_name]
               | map(select(startswith("v'"$PWSH_SERIES"'.")))
               | sort_by(. | ltrimstr("v") | split(".") | map(tonumber))
               | last // empty' 2>/dev/null | sed 's/^v//')" || true
  fi

  if [ -z "$version" ]; then
    if [ "$CHECK_ONLY" -eq 1 ]; then
      warn "could not reach the GitHub releases API to resolve $PWSH_SERIES.x"
      note "Not a host problem — usually a proxy, an egress rule, or the unauthenticated"
      note "rate limit. An install would fall back to $PWSH_FALLBACK_VERSION."
      return
    fi
    version="$PWSH_FALLBACK_VERSION"
    warn "GitHub releases API unreachable — falling back to $version"
    note "This may not be the newest $PWSH_SERIES patch. Pin one with --pwsh-version=X.Y.Z"
    note "once you can reach https://github.com/PowerShell/PowerShell/releases"
  fi

  ok "target version $version"

  local deb="powershell_${version}-1.deb_amd64.deb"
  local url="https://github.com/PowerShell/PowerShell/releases/download/v${version}/${deb}"
  local tmp="/tmp/${deb}"

  if [ "$CHECK_ONLY" -eq 1 ]; then
    note "would download $url"
    note "would install with: apt-get install -y $tmp"
    return
  fi

  run curl -fsSL -o "$tmp" "$url"
  # apt rather than dpkg so dependencies (libicu and friends) resolve from the Ubuntu archive.
  run apt-get install -y "$tmp"
  rm -f "$tmp"

  if command -v pwsh >/dev/null 2>&1; then
    ok "pwsh $(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"
  else
    fail "pwsh not on PATH after installation"
  fi
}

# ── 6. Azure CLI ───────────────────────────────────────────────────────────────────────
#
# Microsoft's apt packages for Azure CLI are tested on Ubuntu 22.04 and 24.04. 26.04 is not
# on that list. Pointing a 26.04 host at the noble repository happens to work today and is
# exactly the kind of undeclared substitution that turns into an unexplainable failure six
# months later, so this script will not do it silently.
azure_cli_apt_available() {
  local codename="${VERSION_CODENAME:-}"
  [ -n "$codename" ] || return 1
  curl -fsI --max-time 15 \
    "https://packages.microsoft.com/repos/azure-cli/dists/${codename}/Release" >/dev/null 2>&1
}

install_azure_cli() {
  heading "Azure CLI"

  if [ "$AZURE_CLI_STRATEGY" = "skip" ]; then
    warn "skipped by request"
    return
  fi

  if command -v az >/dev/null 2>&1; then
    ok "az $(az version --query '\"azure-cli\"' -o tsv 2>/dev/null || echo present)"
    return
  fi

  local strategy="$AZURE_CLI_STRATEGY"

  if [ "$strategy" = "auto" ] || [ "$strategy" = "apt" ]; then
    note "checking whether Microsoft publishes an azure-cli package for ${VERSION_CODENAME:-this release}"
    if azure_cli_apt_available; then
      ok "a ${VERSION_CODENAME} azure-cli repository exists — using apt"
      strategy="apt"
    elif [ "$strategy" = "apt" ]; then
      fail "no azure-cli apt repository for ${VERSION_CODENAME}"
      note "Microsoft's tested apt platforms are Ubuntu 22.04 and 24.04."
      return
    else
      strategy="none"
    fi
  fi

  case "$strategy" in
    apt)
      run install -m 0755 -d /etc/apt/keyrings
      run bash -c 'curl -sLS https://packages.microsoft.com/keys/microsoft.asc |
        gpg --dearmor --yes -o /etc/apt/keyrings/microsoft.gpg'
      run chmod go+r /etc/apt/keyrings/microsoft.gpg
      run bash -c "echo \"Types: deb
URIs: https://packages.microsoft.com/repos/azure-cli/
Suites: ${VERSION_CODENAME}
Components: main
Architectures: $(dpkg --print-architecture)
Signed-by: /etc/apt/keyrings/microsoft.gpg\" > /etc/apt/sources.list.d/azure-cli.sources"
      run apt-get update -qq
      run apt-get install -y azure-cli
      [ "$CHECK_ONLY" -eq 1 ] || { command -v az >/dev/null 2>&1 && ok "az installed" || fail "az not on PATH"; }
      ;;

    container)
      install_azure_cli_container
      ;;

    pipx)
      warn "pipx/PyPI is NOT a Microsoft-supported install path for stable Azure CLI releases."
      note "The Azure CLI project documents pip only for pre-release edge builds."
      note "You asked for it explicitly, so here it is — but prefer the container strategy."
      run apt-get install -y pipx python3-venv
      run bash -c 'pipx install azure-cli'
      note "pipx installs to ~/.local/bin; ensure that is on PATH."
      ;;

    none|*)
      fail "Azure CLI cannot be installed safely on Ubuntu ${VERSION_ID:-?} by this script."
      cat <<EOF

  ${C_BOLD}Why${C_RESET}
    Microsoft's apt packages for Azure CLI are tested on Ubuntu 22.04 and 24.04 only, and no
    azure-cli repository exists for '${VERSION_CODENAME:-this codename}'. This script will not
    point a 26.04 host at the 24.04 (noble) repository: it would probably work, and it would
    be an undeclared substitution that nobody remembers when something breaks later.

  ${C_BOLD}Choose one, explicitly${C_RESET}

    --azure-cli=container   Recommended. Uses the official mcr.microsoft.com/azure-cli image
                            behind a small 'az' wrapper, so the validation scripts call 'az'
                            normally. Fully supported software, no apt mixing, isolated from
                            the host. Requires Docker. Read the limitations the wrapper prints.

    --azure-cli=pipx        Isolated Python venv from PyPI. Works, but the Azure CLI project
                            documents pip for edge builds only, so you are outside the
                            supported path. Opt in knowingly.

    --azure-cli=skip        Install Azure CLI yourself, or run the az-dependent steps from a
                            different machine.

  ${C_BOLD}Re-check later${C_RESET}
    curl -fsI https://packages.microsoft.com/repos/azure-cli/dists/${VERSION_CODENAME:-CODENAME}/Release
    A 200 means Microsoft has published for this release and --azure-cli=apt will work.

EOF
      ;;
  esac
}

install_azure_cli_container() {
  if ! command -v docker >/dev/null 2>&1; then
    fail "the container strategy needs Docker, which is not installed"
    note "sudo apt-get install -y docker.io && sudo usermod -aG docker \$USER"
    return
  fi

  local image="mcr.microsoft.com/azure-cli:azurelinux3.0"
  local wrapper="/usr/local/bin/az"

  if [ "$CHECK_ONLY" -eq 1 ]; then
    note "would pull $image and write the wrapper $wrapper"
    return
  fi

  run docker pull "$image"

  cat > "$wrapper" <<WRAPPER
#!/usr/bin/env bash
#
# Azure CLI, run from the official Microsoft container image.
#
# Written by scripts/bootstrap/ubuntu-26.04.sh because Microsoft's apt packages are not
# tested on Ubuntu 26.04. This is supported Microsoft software, isolated from the host's
# package graph.
#
# LIMITATIONS, which matter for the validation scripts:
#
#   * Only \$HOME and the current working directory are visible inside the container. A
#     --template-file or --src-path outside both will not be found. Run the validation
#     scripts from the repository root, which is what the runbook says to do anyway.
#   * The container runs as your UID so that files it writes (~/.azure, artifacts) stay
#     yours rather than becoming root-owned.
#   * 'az login --use-device-code' works: the device code is printed and you complete it in
#     a browser elsewhere. Interactive browser login cannot work here, and should not be
#     used on a headless host regardless.
#   * Startup costs roughly a second per invocation. Noticeable in loops, harmless here.
#
set -euo pipefail

# --tty is decided when the wrapper RUNS, not when it was written. Baking it in at write
# time gives you a wrapper that either loses the device-code prompt formatting or fails
# with "the input device is not a TTY" the first time a script pipes into az.
tty_flag=()
if [ -t 0 ] && [ -t 1 ]; then tty_flag=(--tty); fi   # not '&&' — under set -e a false test exits

exec docker run --rm --interactive "\${tty_flag[@]}" \\
  --user "\$(id -u):\$(id -g)" \\
  --env HOME=/work/home \\
  --volume "\$HOME:/work/home" \\
  --volume "\$PWD:\$PWD" \\
  --workdir "\$PWD" \\
  $image az "\$@"
WRAPPER

  chmod 0755 "$wrapper"
  ok "wrote $wrapper (official image: $image)"

  if az version >/dev/null 2>&1; then
    ok "az $(az version --query '\"azure-cli\"' -o tsv 2>/dev/null) via container"
  else
    fail "the az wrapper did not run"
    note "Check Docker permissions: docker run --rm $image az version"
  fi
}

# ── 7. Bicep ───────────────────────────────────────────────────────────────────────────
#
# Standalone binary rather than `az bicep install`. With the container strategy, `az bicep
# install` writes into a container layer that is discarded on exit, so it would appear to
# succeed and then never be there. A binary on the host works for every strategy.
install_bicep() {
  heading "Bicep"

  if command -v bicep >/dev/null 2>&1; then
    ok "bicep $(bicep --version 2>/dev/null | head -1)"
  else
    if [ "$CHECK_ONLY" -eq 1 ]; then
      note "would download the Bicep linux-x64 binary to /usr/local/bin/bicep"
    else
      run curl -fsSL -o /usr/local/bin/bicep \
        https://github.com/Azure/bicep/releases/latest/download/bicep-linux-x64
      run chmod +x /usr/local/bin/bicep
      command -v bicep >/dev/null 2>&1 && ok "bicep $(bicep --version | head -1)" || fail "bicep install failed"
    fi
  fi

  # Point the CLI at the host binary so `az bicep build` and `az deployment` use it.
  if command -v az >/dev/null 2>&1; then
    run az config set bicep.use_binary_from_path=true --only-show-errors
    ok "az configured to use the bicep binary from PATH"
  else
    warn "az not available yet — run 'az config set bicep.use_binary_from_path=true' after installing it"
  fi
}

# ── 8. dotnet-ef ───────────────────────────────────────────────────────────────────────
install_dotnet_ef() {
  heading "dotnet-ef"

  local target_home="${SUDO_USER:+/home/$SUDO_USER}"
  target_home="${target_home:-$HOME}"

  if command -v dotnet-ef >/dev/null 2>&1 || [ -x "$target_home/.dotnet/tools/dotnet-ef" ]; then
    ok "dotnet-ef present"
  else
    if [ "$CHECK_ONLY" -eq 1 ]; then
      note "would run: dotnet tool install --global dotnet-ef --version $DOTNET_EF_VERSION"
    elif [ -n "${SUDO_USER:-}" ]; then
      # Global tools are per-user. Installing them as root puts them in /root and the
      # operator then cannot find them — a genuinely confusing ten minutes.
      run sudo -u "$SUDO_USER" -H dotnet tool install --global dotnet-ef --version "$DOTNET_EF_VERSION"
    else
      run dotnet tool install --global dotnet-ef --version "$DOTNET_EF_VERSION"
    fi
  fi

  # ~/.dotnet/tools is not on PATH by default on Ubuntu. The tool installs successfully and
  # is then "not found", which reads as a failed install.
  local profile="$target_home/.bashrc"
  if [ -f "$profile" ] && grep -q '\.dotnet/tools' "$profile"; then
    ok "~/.dotnet/tools already on PATH in $(basename "$profile")"
  elif [ "$CHECK_ONLY" -eq 1 ]; then
    note "would append ~/.dotnet/tools to PATH in $profile"
  else
    echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> "$profile"
    ok "added ~/.dotnet/tools to PATH in $profile"
    note "Open a new shell, or: export PATH=\"\$PATH:\$HOME/.dotnet/tools\""
  fi
}

# ── 9. SQL tooling ─────────────────────────────────────────────────────────────────────
#
# The validation pass talks to SQL through Invoke-Sqlcmd from the SqlServer PowerShell
# module, which uses the managed Microsoft.Data.SqlClient driver. It does NOT need unixODBC
# or msodbcsql18. That matters here, because the Microsoft ODBC driver has no Ubuntu 26.04
# package at the time of writing — so requiring it would block the whole pass for a
# dependency nothing in it actually uses.
#
# --with-odbc installs unixODBC only, for anyone who wants the separate sqlcmd tool.
install_sql_tooling() {
  heading "SQL tooling"

  if ! command -v pwsh >/dev/null 2>&1; then
    if [ "$CHECK_ONLY" -eq 1 ]; then
      note "would install the SqlServer module once pwsh is available"
    else
      fail "pwsh not available — cannot install the SqlServer module"
    fi
    return
  fi

  if [ "$CHECK_ONLY" -eq 1 ]; then
    note "would run: Install-Module SqlServer -Scope CurrentUser -Force"
    note "would verify Invoke-Sqlcmd exposes -AccessToken"
  else
    local as_user=(pwsh -NoProfile -Command)
    if [ -n "${SUDO_USER:-}" ]; then
      as_user=(sudo -u "$SUDO_USER" -H pwsh -NoProfile -Command)
    fi

    "${as_user[@]}" '
      if (-not (Get-Module -ListAvailable -Name SqlServer)) {
          Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
          Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber
      }' || fail "Install-Module SqlServer failed"

    # Capability probe, not a version check. -AccessToken is what 07-TestAppIdentity.ps1
    # depends on, and "a parameter cannot be found that matches parameter name AccessToken"
    # on an older module is a confusing way to discover it three steps into the pass.
    if "${as_user[@]}" '
        Import-Module SqlServer -ErrorAction Stop
        $cmd = Get-Command Invoke-Sqlcmd -ErrorAction Stop
        if (-not $cmd.Parameters.ContainsKey("AccessToken")) { exit 3 }
        exit 0' >/dev/null 2>&1; then
      ok "SqlServer module imports and Invoke-Sqlcmd supports -AccessToken"
    else
      fail "SqlServer module missing, will not import, or Invoke-Sqlcmd has no -AccessToken"
      note "07-TestAppIdentity.ps1 cannot run without it. Try:"
      note "  pwsh -c 'Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber'"
      note "  pwsh -c 'Get-Module -ListAvailable SqlServer | Select Name,Version'"
    fi
  fi

  if [ "$WITH_ODBC" -eq 1 ]; then
    note "installing unixODBC by request"
    run apt-get install -y unixodbc unixodbc-dev
    warn "Microsoft's msodbcsql18 has no Ubuntu 26.04 package at the time of writing."
    note "unixODBC alone does not give you sqlcmd. Nothing in this validation pass needs either."
  else
    ok "unixODBC not required (Invoke-Sqlcmd uses the managed driver)"
  fi
}

# ── 10. Summary ────────────────────────────────────────────────────────────────────────
summary() {
  heading "Summary"

  printf '  %-14s %s\n' "Ubuntu"      "$( . /etc/os-release 2>/dev/null; echo "${VERSION_ID:-?} (${VERSION_CODENAME:-?})" )"
  printf '  %-14s %s\n' "PowerShell"  "$(command -v pwsh   >/dev/null 2>&1 && pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || echo 'not installed')"
  printf '  %-14s %s\n' ".NET SDK"    "$(command -v dotnet >/dev/null 2>&1 && dotnet --version 2>/dev/null || echo 'not installed')"
  printf '  %-14s %s\n' "Azure CLI"   "$(command -v az     >/dev/null 2>&1 && (az version --query '\"azure-cli\"' -o tsv 2>/dev/null || echo present) || echo 'not installed')"
  printf '  %-14s %s\n' "Bicep"       "$(command -v bicep  >/dev/null 2>&1 && bicep --version 2>/dev/null | head -1 || echo 'not installed')"
  printf '  %-14s %s\n' "dotnet-ef"   "$(command -v dotnet-ef >/dev/null 2>&1 && dotnet-ef --version 2>/dev/null | tail -1 || echo 'not on PATH in this shell')"

  echo
  if [ "$FAILURES" -eq 0 ]; then
    printf '%sBootstrap complete%s' "$C_GREEN" "$C_RESET"
    [ "$WARNINGS" -gt 0 ] && printf ' with %d warning(s)' "$WARNINGS"
    printf '.\n'
  else
    printf '%s%d failure(s)%s, %d warning(s).\n' "$C_RED" "$FAILURES" "$C_RESET" "$WARNINGS"
  fi

  if [ "$CHECK_ONLY" -eq 1 ]; then
    printf '\n%s--check: nothing was installed or modified.%s\n' "$C_YELLOW" "$C_RESET"
  else
    cat <<'NEXT'

Next:

  1. Open a new shell so PATH changes take effect.

  2. Sign in. This host is headless, so use the device code flow:

       az login --use-device-code
       az account set --subscription "<dev subscription>"

  3. Run the validation preflight from the repository root:

       pwsh ./scripts/validate/00-Preflight.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

  docs/runbooks/azure-validation.md is authoritative for the order of everything after that.
NEXT
  fi

  [ "$FAILURES" -eq 0 ]
}

# ── Main ───────────────────────────────────────────────────────────────────────────────
printf '%sFC Telecom — Ubuntu %s validation host bootstrap%s\n' "$C_BOLD" "$EXPECTED_VERSION_ID" "$C_RESET"
[ "$CHECK_ONLY" -eq 1 ] && printf '%smode: --check (read-only)%s\n' "$C_YELLOW" "$C_RESET"

require_root
check_host

if [ "$FAILURES" -gt 0 ]; then
  echo
  echo "Host checks failed. Not proceeding." >&2
  exit 1
fi

install_base
configure_dotnet_pinning
install_dotnet
install_powershell
install_azure_cli
install_bicep
install_dotnet_ef
install_sql_tooling
summary
