name: Deploy

on:
  workflow_dispatch:
    inputs:
      environment:
        description: Target environment
        required: true
        default: dev
        type: choice
        options: [dev, prod]
  push:
    branches: [main]

permissions:
  contents: read
  # OIDC federated credentials, not a stored service principal secret. There is no
  # long-lived Azure credential in GitHub or in Azure for this pipeline.
  id-token: write

env:
  DOTNET_NOLOGO: true

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: ${{ inputs.environment || 'dev' }}

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Azure login
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      # ── 1. Infrastructure ────────────────────────────────────────────────────────
      #
      # what-if first, always. The output is in the run log, so an unexpected delete is
      # visible before it happens rather than afterwards.
      - name: Preview infrastructure changes
        run: >-
          az deployment group what-if
          --resource-group ${{ vars.AZURE_RESOURCE_GROUP }}
          --template-file infra/main.bicep
          --parameters infra/main.${{ inputs.environment || 'dev' }}.bicepparam

      - name: Apply infrastructure
        id: infra
        run: >-
          az deployment group create
          --resource-group ${{ vars.AZURE_RESOURCE_GROUP }}
          --template-file infra/main.bicep
          --parameters infra/main.${{ inputs.environment || 'dev' }}.bicepparam
          --query properties.outputs -o json > outputs.json
          && cat outputs.json

      # ── 2. Database migrations ───────────────────────────────────────────────────
      #
      # Applied here as a reviewed idempotent script, NOT by Database.Migrate() at
      # application startup. Startup migration is convenient and it is how two instances
      # race each other into a half-applied schema during a slot swap.
      #
      # Migrations must be additive. Dropping a column is split across two releases —
      # stop writing it, then drop it — so a swap-back is always safe.
      - name: Generate migration script
        run: |
          dotnet tool install --global dotnet-ef
          dotnet ef migrations script --idempotent \
            --project src/FcTelecom.Infrastructure \
            --startup-project src/FcTelecom.Web \
            --output migrate.sql
          echo "--- migration script ---"
          cat migrate.sql

      - name: Apply migrations
        run: |
          SQL_FQDN=$(jq -r '.sqlServerFqdn.value' outputs.json)
          SQL_DB=$(jq -r '.sqlDatabaseName.value' outputs.json)
          ACCESS_TOKEN=$(az account get-access-token --resource https://database.windows.net --query accessToken -o tsv)
          sqlcmd -S "$SQL_FQDN" -d "$SQL_DB" -G -P "$ACCESS_TOKEN" -i migrate.sql

      # ── 3. Application ───────────────────────────────────────────────────────────

      - name: Publish web
        run: dotnet publish src/FcTelecom.Web -c Release -o ./publish/web

      - name: Publish worker
        run: dotnet publish src/FcTelecom.Worker -c Release -o ./publish/worker

      - name: Deploy functions
        uses: azure/functions-action@v1
        with:
          app-name: ${{ fromJson(steps.infra.outputs.stdout || '{}').functionAppName.value || vars.FUNCTION_APP_NAME }}
          package: ./publish/worker

      # Production goes to the staging slot, gets smoke-tested, then swaps. Rollback is a
      # swap back — seconds, not a redeploy.
      - name: Deploy web to staging slot
        if: inputs.environment == 'prod'
        uses: azure/webapps-deploy@v3
        with:
          app-name: ${{ vars.WEB_APP_NAME }}
          slot-name: staging
          package: ./publish/web

      - name: Smoke test the slot
        if: inputs.environment == 'prod'
        run: |
          for attempt in $(seq 1 20); do
            status=$(curl -s -o /dev/null -w "%{http_code}" \
              "https://${{ vars.WEB_APP_NAME }}-staging.azurewebsites.net/health/ready" || true)
            if [ "$status" = "200" ]; then
              echo "Staging slot is ready."
              exit 0
            fi
            echo "Attempt $attempt: /health/ready returned $status. Waiting."
            sleep 15
          done
          echo "::error::Staging slot never reported ready. Not swapping."
          exit 1

      - name: Swap staging into production
        if: inputs.environment == 'prod'
        run: >-
          az webapp deployment slot swap
          --resource-group ${{ vars.AZURE_RESOURCE_GROUP }}
          --name ${{ vars.WEB_APP_NAME }}
          --slot staging
          --target-slot production

      - name: Deploy web directly (dev)
        if: inputs.environment != 'prod'
        uses: azure/webapps-deploy@v3
        with:
          app-name: ${{ vars.WEB_APP_NAME }}
          package: ./publish/web
