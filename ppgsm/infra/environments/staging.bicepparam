using '../main.bicep'

param environmentName = 'staging'
param location = 'westeurope'
param monthlyBudget = 800
param sqlSkuName = 'GP_Gen5_2'
param sqlZoneRedundant = false
param serviceBusSku = 'Premium'
param rawEvidenceRetentionDays = 180
param immutableRawEvidence = true
param logRetentionDays = 180
param apiMinReplicas = 1
param apiMaxReplicas = 5
param workerMaxReplicas = 10
param runtimeAdapterMode = 'SqlBlobServiceBus'
param persistenceMode = 'Sql'
param evidenceStorageMode = 'Blob'
param queueMode = 'ServiceBus'
param enableScheduler = false
param featureFlags = {
  EnableLegacyPowerPlatformManagementApp: false
  EnableScheduledSnapshots: false
  EnableRemediationExecution: false
}
