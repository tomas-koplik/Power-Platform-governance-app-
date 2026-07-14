using '../main.bicep'

param environmentName = 'prod'
param location = 'westeurope'
param monthlyBudget = 1500
param sqlSkuName = 'GP_Gen5_2'
param sqlZoneRedundant = true
param serviceBusSku = 'Premium'
param rawEvidenceRetentionDays = 730
param immutableRawEvidence = true
param exportRetentionDays = 30
param consentDocumentRetentionDays = 2555
param deletionCertificateRetentionDays = 2555
param authoritativeDataRegion = 'westeurope'
param logRetentionDays = 365
param apiMinReplicas = 2
param apiMaxReplicas = 10
param workerMaxReplicas = 20
param runtimeAdapterMode = 'SqlBlobServiceBus'
param persistenceMode = 'Sql'
param evidenceStorageMode = 'Blob'
param queueMode = 'ServiceBus'
param enableScheduler = false
param enableExternalConsentRevocation = false
param externalConsentRevocationApproved = false
param externalConsentRevocationEnterpriseApplicationPolicy = 'Preserve'
param featureFlags = {
  EnableLegacyPowerPlatformManagementApp: false
  EnableScheduledSnapshots: false
  EnableRemediationExecution: false
}
