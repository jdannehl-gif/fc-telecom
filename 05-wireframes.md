name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

permissions:
  contents: read

env:
  DOTNET_NOLOGO: true
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true

jobs:
  build-and-test:
    runs-on: ubuntu-latest

    services:
      # Integration tests run against real SQL Server, applying the real migrations.
      # Tests that run against an in-memory provider prove nothing about a SQL Server
      # deployment — decimal precision, filtered indexes, computed columns, and collation
      # all behave differently, and every one of those matters in this schema.
      sql:
        image: mcr.microsoft.com/mssql/server:2022-latest
        env:
          ACCEPT_EULA: "Y"
          MSSQL_SA_PASSWORD: "CiPipeline!Passw0rd"
          MSSQL_PID: Developer
        ports:
          - 1433:1433
        options: >-
          --health-cmd "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'CiPipeline!Passw0rd' -C -Q 'SELECT 1'"
          --health-interval 10s
          --health-timeout 5s
          --health-retries 12
          --health-start-period 30s

    steps:
      - uses: actions/checkout@v4

      - name: Set up .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Restore
        run: dotnet restore

      - name: Build
        # Warnings are errors, set in Directory.Build.props. A warning nobody has time to
        # read is the same as no warning at all.
        run: dotnet build --no-restore --configuration Release

      - name: Verify formatting
        run: dotnet format --verify-no-changes --no-restore
        continue-on-error: false

      - name: Run tests
        env:
          ConnectionStrings__Default: "Server=localhost,1433;Database=fctelecom_ci;User Id=sa;Password=CiPipeline!Passw0rd;Encrypt=True;TrustServerCertificate=True"
        run: >-
          dotnet test --no-build --configuration Release
          --logger "trx;LogFileName=results.trx"
          --collect:"XPlat Code Coverage"
          --results-directory ./TestResults

      - name: Publish test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results
          path: ./TestResults

  security-scan:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      security-events: write

    steps:
      - uses: actions/checkout@v4

      - name: Set up .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Restore
        run: dotnet restore

      # Fails the build on a High or Critical advisory in any package, direct or
      # transitive. Central package management means the fix is a one-line version bump.
      - name: Check for vulnerable packages
        run: |
          dotnet list package --vulnerable --include-transitive 2>&1 | tee vulnerable.txt
          if grep -qE "High|Critical" vulnerable.txt; then
            echo "::error::Vulnerable packages found at High or Critical severity."
            exit 1
          fi

      - name: Initialize CodeQL
        uses: github/codeql-action/init@v3
        with:
          languages: csharp

      - name: Build for analysis
        run: dotnet build --no-restore --configuration Release

      - name: Run CodeQL
        uses: github/codeql-action/analyze@v3

  validate-infrastructure:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v4

      - name: Build Bicep
        run: |
          az bicep install
          az bicep build --file infra/main.bicep --stdout > /dev/null
          echo "Bicep templates compile."
