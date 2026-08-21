// FC Telecom Manager — subscription-scope entry point.
//
// THIS IS THE ONE TO DEPLOY. `main.bicep` is resource-group-scoped and is consumed here as a
// module; deploying it directly requires the resource group to already exist.
//
//   bicep build infra/subscription.bicep --outfile <out>.json
//   az deployment sub what-if --location eastus2 --template-file <out>.json --parameters ...
//   az deployment sub create  --location eastus2 --template-file <out>.json --parameters ...
//
// WHY THIS FILE EXISTS
//
// The first validation run failed at step 3 with:
//
//   ResourceGroupNotFound: Resource group 'rg-fctelecom-dev' could not be found
//
// The what-if ran resource-group-scoped against a group that the NEXT step was going to
// create. The gate could therefore never pass on a first deployment — the only run where a
// preview matters most, because everything is new and nothing has been reviewed before.
//
// Moving the entry point to subscription scope fixes the ordering at the root rather than by
// creating the group before the preview: the preview now INCLUDES creation of the resource
// group, which is a change worth seeing rather than a precondition to smuggle in beforehand.
//
// Nothing here is configured by hand in the portal. That is what makes the DR runbook a
// redeploy plus a database restore rather than an archaeology exercise.

targetScope = 'subscription'

@description('Short environment name. Comes from infra/main.<env>.bicepparam.')
@allowed(['dev', 'prod'])
param environmentName string

@description('Azure region for the resource group and everything in it.')
param location string

@description('Resource group to create and deploy into, e.g. rg-fctelecom-dev.')
param resourceGroupName string

@description('Entra object ID of the group that should administer Key Vault secrets.')
param keyVaultAdminGroupObjectId string

@description('Entra object ID of the group to set as the SQL Entra administrator.')
param sqlAdminGroupObjectId string

@description('Display name of the SQL Entra administrator group.')
param sqlAdminGroupName string

@description('Monthly budget amount. An ALERT threshold, not a spending cap.')
@minValue(1)
param monthlyBudgetUsd int = 150

@description('Budget alert recipients. Empty disables the budget — deliberately allowed, so a preview can run before anyone has decided who is on the hook.')
param budgetAlertEmails array = []

@description('Budget start, yyyy-MM-01. Azure rejects a change to this on an existing budget, so the deploy script reads the existing value back and passes it unchanged.')
param budgetStartDate string

@description('Budget end, yyyy-MM-01.')
param budgetEndDate string

// The two tags the validation runbook and 99-Cleanup.ps1 both key on. `application` is
// deliberately the short slug `fc-telecom` and not the display name — it is matched by
// scripts, and a display name is a thing people reword.
var resourceGroupTags = {
  application: 'fc-telecom'
  environment: environmentName
  managedBy: 'bicep'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
  tags: resourceGroupTags
}

// The existing resource-group-scoped template, unchanged, as a module. `scope: resourceGroup`
// gives ARM the dependency for free: the group is created first, and what-if expands this
// nested deployment so the preview covers everything inside it as well.
module infrastructure 'main.bicep' = {
  name: 'fctelecom-${environmentName}-infra'
  scope: resourceGroup
  params: {
    environmentName: environmentName
    location: location
    keyVaultAdminGroupObjectId: keyVaultAdminGroupObjectId
    sqlAdminGroupObjectId: sqlAdminGroupObjectId
    sqlAdminGroupName: sqlAdminGroupName
  }
}

module budget 'modules/rg-budget.bicep' = {
  name: 'fctelecom-${environmentName}-budget'
  scope: resourceGroup
  params: {
    budgetName: 'budget-${resourceGroupName}'
    monthlyAmount: monthlyBudgetUsd
    contactEmails: budgetAlertEmails
    startDate: budgetStartDate
    endDate: budgetEndDate
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────────────
//
// Passed straight through from the module. Everything downstream reads these rather than
// reconstructing names, because main.bicep appends a uniqueString() suffix that cannot be
// derived from the environment name.

output resourceGroupName string = resourceGroup.name
output resourceGroupLocation string = resourceGroup.location
output webAppName string = infrastructure.outputs.webAppName
output webAppHostName string = infrastructure.outputs.webAppHostName
output functionAppName string = infrastructure.outputs.functionAppName
output sqlServerFqdn string = infrastructure.outputs.sqlServerFqdn
output sqlDatabaseName string = infrastructure.outputs.sqlDatabaseName
output keyVaultName string = infrastructure.outputs.keyVaultName
output keyVaultUri string = infrastructure.outputs.keyVaultUri
output storageAccountName string = infrastructure.outputs.storageAccountName
output webAppPrincipalId string = infrastructure.outputs.webAppPrincipalId
output functionAppPrincipalId string = infrastructure.outputs.functionAppPrincipalId
output budgetEnabled bool = budget.outputs.budgetEnabled
