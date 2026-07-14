using '../main.bicep'

param environmentName = 'dev'
param location = 'westeurope'
param monthlyBudget = 300
param sqlSkuName = 'GP_S_Gen5_1'
param sqlZoneRedundant = false
param serviceBusSku = 'Standard'
param rawEvidenceRetentionDays = 30
param immutableRawEvidence = false
param logRetentionDays = 30
param apiMinReplicas = 0
param apiMaxReplicas = 2
param workerMaxReplicas = 2
param enableScheduler = false
param featureFlags = {
  EnableLegacyPowerPlatformManagementApp: false
  EnableScheduledSnapshots: false
  EnableRemediationExecution: false
}
