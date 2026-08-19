using './main.bicep'

param environmentName = 'prod'
param location = 'eastus2'

param keyVaultAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
param sqlAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
param sqlAdminGroupName = 'FCTelecom-SQL-Admins'
