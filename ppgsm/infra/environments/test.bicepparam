using '../main.bicep'

param environmentName = 'test'
param runtimeAdapterMode = 'SqlBlobServiceBus'
param persistenceMode = 'Sql'
param evidenceStorageMode = 'Blob'
param queueMode = 'ServiceBus'
param location = 'westeurope'
param monthlyBudget = 400
param sqlSkuName = 'GP_S_Gen5_1'
param sqlZoneRedundant = false
param serviceBusSku = 'Standard'
param rawEvidenceRetentionDays = 60
param immutableRawEvidence = false
param logRetentionDays = 90
param apiMinReplicas = 1
param apiMaxReplicas = 3
param workerMaxReplicas = 5
param enableScheduler = false
param featureFlags = {
  EnableLegacyPowerPlatformManagementApp: false
  EnableScheduledSnapshots: false
  EnableRemediationExecution: false
}
