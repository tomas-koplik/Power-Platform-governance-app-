targetScope = 'subscription'

@allowed(['dev', 'test', 'staging', 'prod'])
param environmentName string
param location string = 'westeurope'
param resourceGroupName string = 'rg-ppgsm-${environmentName}'
param namePrefix string = 'ppgsm-${environmentName}'
param ownerTag string = 'UNCONFIGURED'
param costCenterTag string = 'UNCONFIGURED'
param sqlEntraAdminLogin string = ''
param sqlEntraAdminObjectId string = ''
param authenticationClientId string = ''
param authenticationAuthority string = ''
param authenticationAudience string = ''
param authenticationScope string = ''
param authorizedSpaClientIds array = []
param onboardingWebClientId string = ''
param onboardingConsentCallbackUri string = ''
@secure()
param onboardingSigningSecretUri string = ''
param trustedRuleCatalogVersion string = ''
@secure()
param trustedRuleCatalogAttestation string = ''
param graphVerifierBaseUri string = 'https://graph.microsoft.com/v1.0'
param graphVerifierScopes array = ['https://graph.microsoft.com/.default']
param enableExternalConsentRevocation bool = false
@description('Independent protected-environment approval. Production enablement is rejected unless this is true.')
param externalConsentRevocationApproved bool = false
param externalConsentRevocationGraphBaseUrl string = 'https://graph.microsoft.com/'
param externalConsentRevocationClientApplicationId string = ''
@allowed(['Preserve', 'Disable', 'Remove'])
param externalConsentRevocationEnterpriseApplicationPolicy string = 'Preserve'
param externalConsentRevocationPowerPlatformRbacEndpoint string = ''
@allowed(['https://api.powerplatform.com/.default', 'https://api.bap.microsoft.com/.default'])
param externalConsentRevocationPowerPlatformRbacResourceScope string = 'https://api.powerplatform.com/.default'
param corsAllowedOrigins array = []
param enableAppOnlyCertificate bool = false
param appOnlyClientId string = ''
@secure()
param appOnlyCertificateSecretUri string = ''
param alertEmail string = ''
@description('Digest-pinned image, for example registry.azurecr.io/ppgsm-api@sha256:...')
param apiImage string = 'invalid/ppgsm-api@sha256:0000000000000000000000000000000000000000000000000000000000000000'
@description('Digest-pinned image, for example registry.azurecr.io/ppgsm-worker@sha256:...')
param workerImage string = 'invalid/ppgsm-worker@sha256:0000000000000000000000000000000000000000000000000000000000000000'
@description('Digest-pinned web image built with live adapter configuration.')
param webImage string = 'invalid/ppgsm-web@sha256:0000000000000000000000000000000000000000000000000000000000000000'
@description('Digest-pinned migration image. It may equal the worker digest only after the image implements the migrate command.')
param migrationImage string = 'invalid/ppgsm-migration@sha256:0000000000000000000000000000000000000000000000000000000000000000'
param monthlyBudget int = environmentName == 'prod' ? 1500 : 400
param sqlSkuName string = environmentName == 'prod' ? 'GP_Gen5_2' : 'GP_S_Gen5_1'
param sqlZoneRedundant bool = environmentName == 'prod'
param serviceBusSku string = environmentName == 'prod' ? 'Premium' : 'Standard'
param rawEvidenceRetentionDays int = environmentName == 'prod' ? 730 : 30
param immutableRawEvidence bool = environmentName == 'prod'
@minValue(1)
param exportRetentionDays int = 30
@minValue(1)
param consentDocumentRetentionDays int = environmentName == 'prod' ? 2555 : 365
@minValue(1)
param deletionCertificateRetentionDays int = 2555
param authoritativeDataRegion string = location
param logRetentionDays int = environmentName == 'prod' ? 365 : 90
param apiMinReplicas int = environmentName == 'prod' ? 2 : 1
param apiMaxReplicas int = environmentName == 'prod' ? 10 : 3
param workerMaxReplicas int = environmentName == 'prod' ? 20 : 5
@allowed(['Local', 'SqlBlobServiceBus'])
param runtimeAdapterMode string = environmentName == 'dev' ? 'Local' : 'SqlBlobServiceBus'
@allowed(['Local', 'Sql'])
param persistenceMode string = environmentName == 'dev' ? 'Local' : 'Sql'
@allowed(['Local', 'Blob'])
param evidenceStorageMode string = environmentName == 'dev' ? 'Local' : 'Blob'
@allowed(['Local', 'ServiceBus'])
param queueMode string = environmentName == 'dev' ? 'Local' : 'ServiceBus'
param featureFlags object = {
  EnableLegacyPowerPlatformManagementApp: false
  EnableScheduledSnapshots: false
  EnableRemediationExecution: false
}
param customDomainName string = ''
param certificateKeyVaultSecretId string = ''
param stableRevisionName string = ''
param enableScheduler bool = false

assert productionRuntimeIsDurable = environmentName != 'prod' || (
  runtimeAdapterMode == 'SqlBlobServiceBus'
  && persistenceMode == 'Sql'
  && evidenceStorageMode == 'Blob'
  && queueMode == 'ServiceBus'
)
assert productionFeaturesFailClosed = environmentName != 'prod' || (
  !bool(featureFlags.EnableLegacyPowerPlatformManagementApp)
  && !bool(featureFlags.EnableRemediationExecution)
)
assert imagesAreImmutable = contains(apiImage, '@sha256:') && contains(workerImage, '@sha256:') && contains(webImage, '@sha256:') && contains(migrationImage, '@sha256:')
assert productionIdentityConfigured = environmentName == 'dev' || (
  !empty(authenticationClientId) && startsWith(authenticationAuthority, 'https://') && !empty(authenticationAudience)
  && !empty(authenticationScope) && length(authorizedSpaClientIds) > 0
)
assert productionOnboardingConfigured = environmentName == 'dev' || (
  !empty(onboardingWebClientId) && startsWith(onboardingConsentCallbackUri, 'https://') && startsWith(onboardingSigningSecretUri, 'https://')
)
assert productionTrustConfigured = environmentName == 'dev' || (
  !empty(trustedRuleCatalogVersion) && !empty(trustedRuleCatalogAttestation)
  && startsWith(graphVerifierBaseUri, 'https://') && length(graphVerifierScopes) > 0 && length(corsAllowedOrigins) > 0
)
assert authoritativeRegionMatchesDeployment = authoritativeDataRegion == location
assert productionCallbackOriginAllowed = environmentName == 'dev' || contains(corsAllowedOrigins, replace(onboardingConsentCallbackUri, '/onboarding/callback', ''))
assert appOnlyCertificateIsPocGated = !enableAppOnlyCertificate || (
  !empty(appOnlyClientId) && startsWith(appOnlyCertificateSecretUri, 'https://')
)
assert productionExternalRevocationIsApproved = environmentName != 'prod' || !enableExternalConsentRevocation || externalConsentRevocationApproved
assert externalRevocationIsConfigured = !enableExternalConsentRevocation || (
  externalConsentRevocationGraphBaseUrl == 'https://graph.microsoft.com/'
  && !empty(externalConsentRevocationClientApplicationId)
  && enableAppOnlyCertificate
  && !empty(appOnlyClientId)
  && startsWith(appOnlyCertificateSecretUri, 'https://')
)

var tags = {
  application: 'PPGSM'
  environment: environmentName
  owner: ownerTag
  costCenter: costCenterTag
  dataClassification: 'Confidential'
  managedBy: 'Bicep'
}

var apiRawEvidenceRoleName = guid(subscription().id, 'PPGSM API raw evidence read delete')

resource apiRawEvidenceRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: apiRawEvidenceRoleName
  properties: {
    roleName: 'PPGSM API raw evidence read delete'
    description: 'Read and delete raw evidence blobs without permission to create or overwrite collector evidence.'
    type: 'CustomRole'
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
        ]
        notDataActions: []
      }
    ]
    assignableScopes: [subscription().id]
  }
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module network 'modules/network.bicep' = {
  name: 'network'
  scope: resourceGroup
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
  }
}

module identities 'modules/identities.bicep' = {
  name: 'identities'
  scope: resourceGroup
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
  }
}

module observability 'modules/observability.bicep' = {
  name: 'observability'
  scope: resourceGroup
  params: {
    namePrefix: namePrefix
    location: location
    retentionInDays: logRetentionDays
    alertEmail: alertEmail
    tags: tags
  }
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  scope: resourceGroup
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    apiPrincipalId: identities.outputs.apiPrincipalId
    workerPrincipalId: identities.outputs.workerPrincipalId
    webPrincipalId: identities.outputs.webPrincipalId
    migrationPrincipalId: identities.outputs.migrationPrincipalId
    certificatePrincipalId: identities.outputs.certificatePrincipalId
    apiRawEvidenceRoleId: apiRawEvidenceRole.id
  }
}

module data 'modules/data.bicep' = {
  name: 'data'
  scope: resourceGroup
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
    virtualNetworkId: network.outputs.virtualNetworkId
    workspaceId: observability.outputs.workspaceId
    apiPrincipalId: identities.outputs.apiPrincipalId
    workerPrincipalId: identities.outputs.workerPrincipalId
    migrationPrincipalId: identities.outputs.migrationPrincipalId
    sqlEntraAdminLogin: sqlEntraAdminLogin
    sqlEntraAdminObjectId: sqlEntraAdminObjectId
    tenantId: tenant().tenantId
    sqlSkuName: sqlSkuName
    sqlZoneRedundant: sqlZoneRedundant
    serviceBusSku: serviceBusSku
    rawEvidenceRetentionDays: rawEvidenceRetentionDays
    immutableRawEvidence: immutableRawEvidence
    exportRetentionDays: exportRetentionDays
  }
}

module compute 'modules/compute.bicep' = {
  name: 'compute'
  scope: resourceGroup
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    infrastructureSubnetId: network.outputs.infrastructureSubnetId
    workspaceId: observability.outputs.workspaceId
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    registryLoginServer: registry.outputs.loginServer
    apiIdentityId: identities.outputs.apiIdentityId
    workerIdentityId: identities.outputs.workerIdentityId
    webIdentityId: identities.outputs.webIdentityId
    migrationIdentityId: identities.outputs.migrationIdentityId
    certificateIdentityId: identities.outputs.certificateIdentityId
    apiImage: apiImage
    workerImage: workerImage
    webImage: webImage
    migrationImage: migrationImage
    authenticationClientId: authenticationClientId
    authenticationAuthority: authenticationAuthority
    authenticationAudience: authenticationAudience
    authenticationScope: authenticationScope
    authorizedSpaClientIds: authorizedSpaClientIds
    onboardingWebClientId: onboardingWebClientId
    onboardingConsentCallbackUri: onboardingConsentCallbackUri
    onboardingSigningSecretUri: onboardingSigningSecretUri
    trustedRuleCatalogVersion: trustedRuleCatalogVersion
    trustedRuleCatalogAttestation: trustedRuleCatalogAttestation
    graphVerifierBaseUri: graphVerifierBaseUri
    graphVerifierScopes: graphVerifierScopes
    enableExternalConsentRevocation: enableExternalConsentRevocation
    externalConsentRevocationGraphBaseUrl: externalConsentRevocationGraphBaseUrl
    externalConsentRevocationClientApplicationId: externalConsentRevocationClientApplicationId
    externalConsentRevocationEnterpriseApplicationPolicy: externalConsentRevocationEnterpriseApplicationPolicy
    externalConsentRevocationPowerPlatformRbacEndpoint: externalConsentRevocationPowerPlatformRbacEndpoint
    externalConsentRevocationPowerPlatformRbacResourceScope: externalConsentRevocationPowerPlatformRbacResourceScope
    corsAllowedOrigins: corsAllowedOrigins
    enableAppOnlyCertificate: enableAppOnlyCertificate
    appOnlyClientId: appOnlyClientId
    appOnlyCertificateSecretUri: appOnlyCertificateSecretUri
    blobEndpoint: data.outputs.blobEndpoint
    serviceBusFqdn: data.outputs.serviceBusFqdn
    snapshotQueueName: data.outputs.snapshotQueueName
    keyVaultUri: data.outputs.daemonKeyVaultUri
    sqlServerFqdn: data.outputs.sqlServerFqdn
    sqlDatabaseName: data.outputs.sqlDatabaseName
    featureFlags: featureFlags
    runtimeAdapterMode: runtimeAdapterMode
    persistenceMode: persistenceMode
    evidenceStorageMode: evidenceStorageMode
    queueMode: queueMode
    customDomainName: customDomainName
    certificateKeyVaultSecretId: certificateKeyVaultSecretId
    stableRevisionName: stableRevisionName
    enableScheduler: enableScheduler
    apiMinReplicas: apiMinReplicas
    apiMaxReplicas: apiMaxReplicas
    workerMaxReplicas: workerMaxReplicas
    authoritativeDataRegion: authoritativeDataRegion
    consentDocumentRetentionDays: consentDocumentRetentionDays
    deletionCertificateRetentionDays: deletionCertificateRetentionDays
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${namePrefix}-monthly-budget'
  scope: resourceGroup
  properties: {
    amount: monthlyBudget
    category: 'Cost'
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: '${utcNow('yyyy-MM')}-01T00:00:00Z'
      endDate: '2035-12-31T00:00:00Z'
    }
    notifications: {
      Actual80: {
        contactEmails: []
        contactGroups: [observability.outputs.actionGroupId]
        enabled: true
        locale: 'en-us'
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
      }
      Forecast100: {
        contactEmails: []
        contactGroups: [observability.outputs.actionGroupId]
        enabled: true
        locale: 'en-us'
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
      }
    }
  }
}

resource apiExisting 'Microsoft.App/containerApps@2024-03-01' existing = {
  scope: resourceGroup
  name: compute.outputs.apiName
}

resource serviceBusExisting 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  scope: resourceGroup
  name: data.outputs.serviceBusNamespace
}

resource apiRestartAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-api-restarts'
  scope: resourceGroup
  location: 'global'
  tags: tags
  properties: {
    actions: [
      {
        actionGroupId: observability.outputs.actionGroupId
      }
    ]
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          metricName: 'RestartCount'
          metricNamespace: 'Microsoft.App/containerApps'
          name: 'RestartCount'
          operator: 'GreaterThan'
          threshold: 3
          timeAggregation: 'Total'
        }
      ]
    }
    description: 'API revision restarted more than three times in fifteen minutes.'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [apiExisting.id]
    severity: 2
    windowSize: 'PT15M'
  }
}

resource deadLetterAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-dead-lettered-jobs'
  scope: resourceGroup
  location: 'global'
  tags: tags
  properties: {
    actions: [
      {
        actionGroupId: observability.outputs.actionGroupId
      }
    ]
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: [data.outputs.snapshotQueueName]
            }
          ]
          metricName: 'DeadletteredMessages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          name: 'DeadletteredMessages'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Average'
        }
      ]
    }
    description: 'Snapshot jobs entered the dead-letter queue.'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [serviceBusExisting.id]
    severity: 1
    windowSize: 'PT15M'
  }
}

resource queueDepthAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-queue-backlog'
  scope: resourceGroup
  location: 'global'
  tags: tags
  properties: {
    actions: [
      {
        actionGroupId: observability.outputs.actionGroupId
      }
    ]
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: [data.outputs.snapshotQueueName]
            }
          ]
          metricName: 'ActiveMessages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          name: 'ActiveMessages'
          operator: 'GreaterThan'
          threshold: 100
          timeAggregation: 'Average'
        }
      ]
    }
    description: 'Snapshot queue backlog indicates collection delay; verify actual oldest-message age in telemetry.'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [serviceBusExisting.id]
    severity: 2
    windowSize: 'PT15M'
  }
}

resource queueAgeAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-queue-oldest-message-age'
  scope: resourceGroup
  location: location
  tags: tags
  properties: {
    actions: {
      actionGroups: [observability.outputs.actionGroupId]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    criteria: {
      allOf: [
        {
          dimensions: []
          failingPeriods: {
            minFailingPeriodsToAlert: 2
            numberOfEvaluationPeriods: 2
          }
          operator: 'GreaterThan'
          query: 'AppMetrics | where TimeGenerated > ago(15m) | where Name == "ppgsm.queue.oldest_message_age_seconds" | extend QueueName=tostring(Properties.queue_name), Environment=tostring(Properties.environment) | where QueueName == "${data.outputs.snapshotQueueName}" and Environment == "${environmentName}" | summarize AgeSeconds=max(Sum)'
          resourceIdColumn: ''
          threshold: 900
          timeAggregation: 'Maximum'
        }
      ]
    }
    description: 'The oldest snapshot job has remained queued for more than fifteen minutes. Metric unit is seconds.'
    displayName: 'PPGSM oldest queue message age'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [observability.outputs.workspaceId]
    severity: 1
    skipQueryValidation: true
    windowSize: 'PT15M'
  }
}

resource collectionFailureAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-collection-failures'
  scope: resourceGroup
  location: location
  tags: tags
  properties: {
    actions: {
      actionGroups: [observability.outputs.actionGroupId]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    criteria: {
      allOf: [
        {
          dimensions: []
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
          operator: 'GreaterThan'
          query: 'AppTraces | where TimeGenerated > ago(15m) | where Message has_any ("snapshot failed", "collector failed") | summarize failures=count()'
          resourceIdColumn: ''
          threshold: 0
          timeAggregation: 'Count'
        }
      ]
    }
    description: 'Snapshot or collector failure reported by application telemetry.'
    displayName: 'PPGSM collection failures'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [observability.outputs.workspaceId]
    severity: 1
    skipQueryValidation: true
    windowSize: 'PT15M'
  }
}

resource exportFailureAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-export-failures'
  scope: resourceGroup
  location: location
  tags: tags
  properties: {
    actions: {
      actionGroups: [observability.outputs.actionGroupId]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    criteria: {
      allOf: [
        {
          dimensions: []
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
          operator: 'GreaterThan'
          query: 'AppTraces | where TimeGenerated > ago(15m) | where Message startswith "Export " and Message has " failed for tenant " | summarize failures=count()'
          resourceIdColumn: ''
          threshold: 0
          timeAggregation: 'Count'
        }
      ]
    }
    description: 'A tenant export failed. Telemetry contains identifiers and exception metadata, never export payload content.'
    displayName: 'PPGSM export failures'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [observability.outputs.workspaceId]
    severity: 1
    skipQueryValidation: true
    windowSize: 'PT15M'
  }
}

resource exportAgeAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-export-age'
  scope: resourceGroup
  location: location
  tags: tags
  properties: {
    actions: {
      actionGroups: [observability.outputs.actionGroupId]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    criteria: {
      allOf: [
        {
          dimensions: []
          failingPeriods: {
            minFailingPeriodsToAlert: 2
            numberOfEvaluationPeriods: 2
          }
          operator: 'GreaterThan'
          query: 'AppTraces | where TimeGenerated > ago(30m) | where Message startswith "Export " and Message has " queued beyond service objective " | summarize stale=count()'
          resourceIdColumn: ''
          threshold: 0
          timeAggregation: 'Count'
        }
      ]
    }
    description: 'An export remained queued beyond the fifteen-minute service objective.'
    displayName: 'PPGSM stale exports'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [observability.outputs.workspaceId]
    severity: 2
    skipQueryValidation: true
    windowSize: 'PT30M'
  }
}

resource throttlingAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-upstream-throttling'
  scope: resourceGroup
  location: location
  tags: tags
  properties: {
    actions: {
      actionGroups: [observability.outputs.actionGroupId]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    criteria: {
      allOf: [
        {
          dimensions: []
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
          operator: 'GreaterThan'
          query: 'AppDependencies | where TimeGenerated > ago(15m) | where ResultCode == "429" | summarize throttled=count()'
          resourceIdColumn: ''
          threshold: 10
          timeAggregation: 'Count'
        }
      ]
    }
    description: 'Power Platform or Graph dependencies returned repeated HTTP 429 responses.'
    displayName: 'PPGSM upstream throttling'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [observability.outputs.workspaceId]
    severity: 2
    skipQueryValidation: true
    windowSize: 'PT15M'
  }
}

resource anomalousAccessAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-anomalous-tenant-access'
  scope: resourceGroup
  location: location
  tags: tags
  properties: {
    actions: {
      actionGroups: [observability.outputs.actionGroupId]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    criteria: {
      allOf: [
        {
          dimensions: []
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
          operator: 'GreaterThan'
          query: 'AppRequests | where TimeGenerated > ago(15m) | extend TenantId=tostring(Properties.TenantId) | where isnotempty(TenantId) | summarize TenantCount=dcount(TenantId), RequestCount=count() by UserId=tostring(UserId) | where TenantCount > 10 or RequestCount > 1000 | count'
          resourceIdColumn: ''
          threshold: 0
          timeAggregation: 'Count'
        }
      ]
    }
    description: 'One identity accessed an unusual number of tenants or records; tenant identifiers are retained without payload content.'
    displayName: 'PPGSM anomalous tenant access'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [observability.outputs.workspaceId]
    severity: 1
    skipQueryValidation: true
    windowSize: 'PT15M'
  }
}

resource certificateExpiryAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-certificate-expiry'
  scope: resourceGroup
  location: location
  tags: tags
  properties: {
    actions: {
      actionGroups: [observability.outputs.actionGroupId]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    criteria: {
      allOf: [
        {
          dimensions: []
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
          operator: 'LessThan'
          query: 'AppMetrics | where TimeGenerated > ago(6h) | where Name == "ppgsm.certificate.days_to_expiry" | extend CertificateId=tostring(Properties.certificate_id), Environment=tostring(Properties.environment) | where Environment == "${environmentName}" and isnotempty(CertificateId) | summarize DaysToExpiry=min(Sum)'
          resourceIdColumn: ''
          threshold: 30
          timeAggregation: 'Minimum'
        }
      ]
    }
    description: 'A configured ingress or app-only certificate has fewer than thirty days remaining. Metric unit is days.'
    displayName: 'PPGSM certificate expiry'
    enabled: true
    evaluationFrequency: 'PT1H'
    scopes: [observability.outputs.workspaceId]
    severity: 1
    skipQueryValidation: true
    windowSize: 'PT6H'
  }
}

output webUrl string = compute.outputs.webUrl
output spaRedirectUri string = compute.outputs.spaRedirectUri
output spaLogoutUri string = compute.outputs.spaLogoutUri

output resourceGroupName string = resourceGroup.name
output apiUrl string = compute.outputs.apiUrl
output apiName string = compute.outputs.apiName
output workerName string = compute.outputs.workerName
output migrationJobName string = compute.outputs.migrationJobName
output schedulerJobName string = compute.outputs.schedulerJobName
output exportJobName string = compute.outputs.exportJobName
output registryName string = registry.outputs.registryName
output daemonKeyVaultName string = data.outputs.daemonKeyVaultName
output sqlServerFqdn string = data.outputs.sqlServerFqdn
output sqlDatabaseName string = data.outputs.sqlDatabaseName
output storageAccountName string = data.outputs.storageAccountName
output serviceBusNamespace string = data.outputs.serviceBusNamespace
output actionGroupId string = observability.outputs.actionGroupId
