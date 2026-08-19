using './main.bicep'

param environmentName = 'dev'
param location = 'eastus2'

// Entra group object IDs. Object IDs, never display names — a group rename must not
// silently move who can read the vault.
param keyVaultAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
param sqlAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
param sqlAdminGroupName = 'FCTelecom-SQL-Admins'
