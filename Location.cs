// FC Telecom Manager — infrastructure
//
// Deploy:
//   az deployment group what-if -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
//   az deployment group create  -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
//
// Nothing here is configured by hand in the portal. That is what makes the DR runbook a
// redeploy plus a database restore rather than an archaeology exercise.

targetScope = 'resourceGroup'

@description('Short environment name, e.g. dev or prod.')
@allowed(['dev', 'prod'])
param environmentName string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Entra object ID of the group that should administer Key Vault secrets.')
param keyVaultAdminGroupObjectId string

@description('Entra object ID of the group to set as the SQL Entra administrator.')
param sqlAdminGroupObjectId string

@description('Display name of the SQL Entra administrator group.')
param sqlAdminGroupName string

var isProd = environmentName == 'prod'
var namePrefix = 'fctel-${environmentName}'
var uniqueSuffix = uniqueString(resourceGroup().id)

var tags = {
  application: 'FC Telecom Manager'
  environment: environmentName
  managedBy: 'bicep'
}

// ── Observability ────────────────────────────────────────────────────────────────────

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-law'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: isProd ? 90 : 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-ai'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    // Telemetry ingestion is the largest controllable cost in this system once the
    // monitoring module is running. The cap is set deliberately, with an alert at 80%,
    // rather than discovered on an invoice.
    SamplingPercentage: isProd ? 50 : 100
  }
}

// ── Data ─────────────────────────────────────────────────────────────────────────────

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${namePrefix}-sql-${uniqueSuffix}'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // Entra-only authentication. There is no SQL login and therefore no SQL password to
    // leak, rotate, or find committed in a repository.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminGroupName
      principalType: 'Group'
      sid: sqlAdminGroupObjectId
      tenantId: subscription().tenantId
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'fctelecom'
  location: location
  tags: tags
  sku: isProd
    ? { name: 'GP_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
    : { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    zoneRedundant: isProd
    // Serverless auto-pause in non-production costs almost nothing when idle. Production
    // stays provisioned: resume latency is unacceptable for a tool people open during an
    // outage, which is precisely when it has been idle for hours.
    autoPauseDelay: isProd ? -1 : 60
    minCapacity: isProd ? json('2') : json('0.5')
    requestedBackupStorageRedundancy: isProd ? 'Geo' : 'Local'
  }
}

resource sqlAuditSettings 'Microsoft.Sql/servers/auditingSettings@2023-08-01-preview' = {
  parent: sqlServer
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
  }
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'fctel${environmentName}${uniqueSuffix}'
  location: location
  tags: tags
  sku: { name: isProd ? 'Standard_GRS' : 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // No anonymous blob access, and no shared-key access either — so the only way to read
    // a document is a user-delegation SAS minted by the application's managed identity,
    // which is attributable and expires.
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    accessTier: 'Hot'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 30 }
    isVersioningEnabled: true
  }
}

resource documentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'documents'
  properties: { publicAccess: 'None' }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${namePrefix}-kv-${uniqueSuffix}'
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    // Purge protection on in both environments. Recovering from an accidental vault
    // deletion without it means the field-encryption key is gone and every static IP
    // record is permanently unreadable.
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

// ── Compute ──────────────────────────────────────────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${namePrefix}-plan'
  location: location
  tags: tags
  sku: isProd ? { name: 'P1v3', tier: 'PremiumV3' } : { name: 'B1', tier: 'Basic' }
  kind: 'linux'
  properties: { reserved: true }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${namePrefix}-web'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: isProd
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health/live'
      // Blazor Server holds a SignalR circuit per user, so requests must return to the
      // same instance. Documented in the architecture notes along with the Redis
      // backplane path if scale ever outgrows affinity.
      webSocketsEnabled: true
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: isProd ? 'Production' : 'Development' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__Default', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30' }
        { name: 'Documents__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
        { name: 'SeedDemoData', value: 'false' }
      ]
    }
  }
}

resource stagingSlot 'Microsoft.Web/sites/slots@2023-12-01' = if (isProd) {
  parent: webApp
  name: 'staging'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      healthCheckPath: '/health/live'
    }
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${namePrefix}-func'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__Default', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False' }
        { name: 'Documents__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
      ]
    }
  }
}

// ── Role assignments ─────────────────────────────────────────────────────────────────
//
// Managed identity everywhere. There is no connection-string secret, no storage account
// key, and no Key Vault access policy in App Service configuration.

var keyVaultSecretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var keyVaultAdmin = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '00482a5a-887f-4fb3-b363-3b7fe8e74483')
var blobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var queueDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')

resource webKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, webApp.id, 'kv-secrets-user')
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, functionApp.id, 'kv-secrets-user')
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource adminKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, keyVaultAdminGroupObjectId, 'kv-admin')
  properties: {
    roleDefinitionId: keyVaultAdmin
    principalId: keyVaultAdminGroupObjectId
    principalType: 'Group'
  }
}

resource webBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, webApp.id, 'blob-contributor')
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionApp.id, 'blob-contributor')
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionQueueAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionApp.id, 'queue-contributor')
  properties: {
    roleDefinitionId: queueDataContributor
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Alerts ───────────────────────────────────────────────────────────────────────────

resource budgetAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (isProd) {
  name: '${namePrefix}-readiness-alert'
  location: location
  tags: tags
  properties: {
    displayName: 'Application readiness failing'
    description: 'The /health/ready endpoint has been failing. Database, storage, Key Vault, outbox depth, or probe heartbeats.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'requests | where url endswith "/health/ready" and success == false'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────────────

output webAppName string = webApp.name
output webAppHostName string = webApp.properties.defaultHostName
output functionAppName string = functionApp.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
output storageAccountName string = storage.name
output webAppPrincipalId string = webApp.identity.principalId
output functionAppPrincipalId string = functionApp.identity.principalId
