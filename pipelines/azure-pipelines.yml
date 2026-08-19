# Azure DevOps equivalent of .github/workflows/ci.yml and cd.yml.
#
# Maintained deliberately so the CI/CD choice stays reversible. If you standardise on
# Azure DevOps, delete the .github/workflows directory; if you standardise on GitHub
# Actions, delete this file. Keeping both alive without using both is how they drift.

trigger:
  branches:
    include: [main]

pr:
  branches:
    include: [main]

variables:
  buildConfiguration: Release
  DOTNET_NOLOGO: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true

stages:
  - stage: Build
    displayName: Build and test
    jobs:
      - job: BuildTest
        pool:
          vmImage: ubuntu-latest

        services:
          sql: sqlserver

        steps:
          - task: UseDotNet@2
            inputs:
              packageType: sdk
              useGlobalJson: true

          - script: dotnet restore
            displayName: Restore

          - script: dotnet build --no-restore -c $(buildConfiguration)
            displayName: Build (warnings are errors)

          - script: dotnet format --verify-no-changes --no-restore
            displayName: Verify formatting

          - script: >-
              dotnet test --no-build -c $(buildConfiguration)
              --logger trx --collect:"XPlat Code Coverage"
              --results-directory $(Agent.TempDirectory)/TestResults
            displayName: Test
            env:
              ConnectionStrings__Default: "Server=localhost,1433;Database=fctelecom_ci;User Id=sa;Password=$(sqlPassword);Encrypt=True;TrustServerCertificate=True"

          - task: PublishTestResults@2
            condition: succeededOrFailed()
            inputs:
              testResultsFormat: VSTest
              testResultsFiles: '**/*.trx'
              searchFolder: $(Agent.TempDirectory)/TestResults

          - script: |
              dotnet list package --vulnerable --include-transitive 2>&1 | tee vulnerable.txt
              if grep -qE "High|Critical" vulnerable.txt; then
                echo "##vso[task.logissue type=error]Vulnerable packages at High or Critical severity."
                exit 1
              fi
            displayName: Dependency vulnerability scan

          - script: |
              dotnet publish src/FcTelecom.Web -c $(buildConfiguration) -o $(Build.ArtifactStagingDirectory)/web
              dotnet publish src/FcTelecom.Worker -c $(buildConfiguration) -o $(Build.ArtifactStagingDirectory)/worker
              cp -r infra $(Build.ArtifactStagingDirectory)/infra
            displayName: Publish artifacts

          - publish: $(Build.ArtifactStagingDirectory)
            artifact: drop

  - stage: DeployDev
    displayName: Deploy to dev
    dependsOn: Build
    condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
    jobs:
      - deployment: Dev
        environment: fctelecom-dev
        pool:
          vmImage: ubuntu-latest
        strategy:
          runOnce:
            deploy:
              steps:
                - template: steps/deploy.yml
                  parameters:
                    environmentName: dev

  - stage: DeployProd
    displayName: Deploy to production
    dependsOn: DeployDev
    condition: succeeded()
    jobs:
      # The 'fctelecom-prod' environment carries a manual approval check, configured in
      # Azure DevOps rather than here — approvals belong to the environment, not the YAML.
      - deployment: Prod
        environment: fctelecom-prod
        pool:
          vmImage: ubuntu-latest
        strategy:
          runOnce:
            deploy:
              steps:
                - template: steps/deploy.yml
                  parameters:
                    environmentName: prod
                    useSlot: true
