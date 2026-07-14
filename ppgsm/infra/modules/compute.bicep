param namePrefix string
param location string
param tags object
param infrastructureSubnetId string
param workspaceId string
param appInsightsConnectionString string
param registryLoginServer string
param apiIdentityId string
param workerIdentityId string
param webIdentityId string
param migrationIdentityId string
param certificateIdentityId string
param apiImage string
param workerImage string
param webImage string
param migrationImage string
param authenticationClientId string
param authenticationAuthority string
param authenticationAudience string
param authenticationScope string
param authorizedSpaClientIds array
param onboardingWebClientId string
param onboardingConsentCallbackUri string
param onboardingSigningSecretUri string
param trustedRuleCatalogVersion string
param trustedRuleCatalogAttestation string
param graphVerifierBaseUri string
param graphVerifierScopes array
param enableExternalConsentRevocation bool = false
param externalConsentRevocationGraphBaseUrl string = 'https://graph.microsoft.com/'
param externalConsentRevocationClientApplicationId string = ''
@allowed(['Preserve', 'Disable', 'Remove'])
param externalConsentRevocationEnterpriseApplicationPolicy string = 'Preserve'
param externalConsentRevocationPowerPlatformRbacEndpoint string = ''
@allowed(['https://api.powerplatform.com/.default', 'https://api.bap.microsoft.com/.default'])
param externalConsentRevocationPowerPlatformRbacResourceScope string = 'https://api.powerplatform.com/.default'
param corsAllowedOrigins array
param enableAppOnlyCertificate bool = false
param appOnlyClientId string = ''
param appOnlyCertificateSecretUri string = ''
param blobEndpoint string
param serviceBusFqdn string
param snapshotQueueName string
param keyVaultUri string
param sqlServerFqdn string
param sqlDatabaseName string
param featureFlags object = {}
param runtimeAdapterMode string
param persistenceMode string
param evidenceStorageMode string
param queueMode string
param customDomainName string = ''
param certificateKeyVaultSecretId string = ''
param stableRevisionName string = ''
param enableScheduler bool = false
param apiMinReplicas int = 1
param apiMaxReplicas int = 3
param workerMaxReplicas int = 5
param authoritativeDataRegion string
param consentDocumentRetentionDays int
param deletionCertificateRetentionDays int

var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var commonEnv = [
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'Azure__BlobEndpoint'
    value: blobEndpoint
  }
  {
    name: 'Azure__ServiceBusFqdn'
    value: serviceBusFqdn
  }
  {
    name: 'Azure__SnapshotQueueName'
    value: snapshotQueueName
  }
  {
    name: 'Azure__KeyVaultUri'
    value: keyVaultUri
  }
  {
    name: 'ConnectionStrings__Ppgsm'
    value: sqlConnectionString
  }
  {
    name: 'Runtime__AdapterMode'
    value: runtimeAdapterMode
  }
  {
    name: 'Runtime__PersistenceMode'
    value: persistenceMode
  }
  {
    name: 'Runtime__EvidenceStorageMode'
    value: evidenceStorageMode
  }
  {
    name: 'Runtime__QueueMode'
    value: queueMode
  }
]
var featureFlagEnv = [for flag in items(featureFlags): {
  name: 'FeatureManagement__${flag.key}'
  value: string(flag.value)
}]
var authorizedClientEnv = [for (clientId, index) in authorizedSpaClientIds: {
  name: 'Authentication__ApiAccess__AuthorizedClientIds__${index}'
  value: clientId
}]
var graphScopeEnv = [for (scope, index) in graphVerifierScopes: {
  name: 'GraphVerifier__Scopes__${index}'
  value: scope
}]
var corsOriginEnv = [for (origin, index) in corsAllowedOrigins: {
  name: 'Cors__AllowedOrigins__${index}'
  value: origin
}]
var appOnlyCertificateEnv = [
  {
    name: 'Collectors__AppOnlyCertificate__Enabled'
    value: string(enableAppOnlyCertificate)
  }
  {
    name: 'Collectors__AppOnlyCertificate__ClientId'
    value: appOnlyClientId
  }
  {
    name: 'Collectors__AppOnlyCertificate__KeyVaultCertificateUri'
    value: appOnlyCertificateSecretUri
  }
]
var externalConsentRevocationEnv = [
  {
    name: 'Offboarding__ExternalConsentRevocation__Enabled'
    value: string(enableExternalConsentRevocation)
  }
  {
    name: 'Offboarding__ExternalConsentRevocation__GraphBaseUrl'
    value: externalConsentRevocationGraphBaseUrl
  }
  {
    name: 'Offboarding__ExternalConsentRevocation__ClientApplicationId'
    value: externalConsentRevocationClientApplicationId
  }
  {
    name: 'Offboarding__ExternalConsentRevocation__EnterpriseApplicationPolicy'
    value: externalConsentRevocationEnterpriseApplicationPolicy
  }
  {
    name: 'Offboarding__ExternalConsentRevocation__PowerPlatformRbacEndpoint'
    value: externalConsentRevocationPowerPlatformRbacEndpoint
  }
  {
    name: 'Offboarding__ExternalConsentRevocation__PowerPlatformRbacResourceScope'
    value: externalConsentRevocationPowerPlatformRbacResourceScope
  }
]

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-cae'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${certificateIdentityId}': {}
    }
  }
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(workspaceId, '2023-09-01').customerId
        sharedKey: listKeys(workspaceId, '2023-09-01').primarySharedKey
      }
    }
    infrastructureSubnetId: infrastructureSubnetId
    internal: false
    peerAuthentication: {
      mtls: {
        enabled: false
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource managedCertificate 'Microsoft.App/managedEnvironments/certificates@2024-03-01' = if (!empty(certificateKeyVaultSecretId)) {
  parent: environment
  name: '${namePrefix}-tls'
  location: location
  properties: {
    certificateKeyVaultProperties: {
      identity: certificateIdentityId
      keyVaultUrl: certificateKeyVaultSecretId
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Multiple'
      secrets: [
        {
          name: 'onboarding-state-signing-key'
          keyVaultUrl: onboardingSigningSecretUri
          identity: apiIdentityId
        }
      ]
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8080
        traffic: empty(stableRevisionName) ? [
            {
              label: 'stable'
              latestRevision: true
              weight: 100
            }
          ] : [
            {
              label: 'stable'
              revisionName: stableRevisionName
              weight: 100
            }
          ]
        transport: 'auto'
      }
      registries: [
        {
          identity: apiIdentityId
          server: registryLoginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          env: concat(commonEnv, featureFlagEnv, authorizedClientEnv, graphScopeEnv, corsOriginEnv, appOnlyCertificateEnv, externalConsentRevocationEnv, [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'Authentication__ClientId'
              value: authenticationClientId
            }
            {
              name: 'Authentication__Authority'
              value: authenticationAuthority
            }
            {
              name: 'Authentication__Audience'
              value: authenticationAudience
            }
            {
              name: 'Authentication__ApiAccess__Scope'
              value: authenticationScope
            }
            {
              name: 'Onboarding__WebClientId'
              value: onboardingWebClientId
            }
            {
              name: 'Onboarding__ConsentCallbackUri'
              value: onboardingConsentCallbackUri
            }
            {
              name: 'DataGovernance__AuthoritativeRegion'
              value: authoritativeDataRegion
            }
            {
              name: 'DataGovernance__ConsentDocumentRetentionDays'
              value: string(consentDocumentRetentionDays)
            }
            {
              name: 'DataGovernance__DeletionCertificateRetentionDays'
              value: string(deletionCertificateRetentionDays)
            }
            {
              name: 'Onboarding__State__SigningKey'
              secretRef: 'onboarding-state-signing-key'
            }
            {
              name: 'RuleCatalog__TrustedVersion'
              value: trustedRuleCatalogVersion
            }
            {
              name: 'RuleCatalog__TrustedManifestDigests'
              value: trustedRuleCatalogAttestation
            }
            {
              name: 'GraphVerifier__BaseUri'
              value: graphVerifierBaseUri
            }
          ])
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
              }
              failureThreshold: 12
              periodSeconds: 5
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: apiMinReplicas
        maxReplicas: apiMaxReplicas
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-web'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${webIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8080
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
        transport: 'auto'
      }
      registries: [
        {
          identity: webIdentityId
          server: registryLoginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          env: [
            {
              name: 'PPGSM_ENTRA_CLIENT_ID'
              value: authorizedSpaClientIds[0]
            }
            {
              name: 'PPGSM_ENTRA_AUTHORITY'
              value: authenticationAuthority
            }
            {
              name: 'PPGSM_API_SCOPE'
              value: authenticationScope
            }
            {
              name: 'PPGSM_API_BASE_URL'
              value: 'https://${api.properties.configuration.ingress.fqdn}'
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 5
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 30
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

resource customDomain 'Microsoft.App/containerApps/authConfigs@2024-03-01' = if (!empty(customDomainName) && !empty(certificateKeyVaultSecretId)) {
  parent: api
  name: 'current'
  properties: {
    platform: {
      enabled: false
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-worker'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          identity: workerIdentityId
          server: registryLoginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          env: concat(commonEnv, featureFlagEnv, appOnlyCertificateEnv)
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: workerMaxReplicas
        rules: [
          {
            name: 'snapshot-queue'
            custom: {
              type: 'azure-servicebus'
              identity: workerIdentityId
              metadata: {
                namespace: serviceBusFqdn
                queueName: snapshotQueueName
                messageCount: '5'
              }
            }
          }
        ]
      }
    }
  }
}

resource scheduler 'Microsoft.App/jobs@2024-03-01' = if (enableScheduler) {
  name: '${namePrefix}-scheduler'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      replicaRetryLimit: 2
      replicaTimeout: 600
      scheduleTriggerConfig: {
        cronExpression: '0 2 * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          identity: workerIdentityId
          server: registryLoginServer
        }
      ]
      triggerType: 'Schedule'
    }
    template: {
      containers: [
        {
          name: 'scheduler'
          image: workerImage
          args: ['schedule']
          env: concat(commonEnv, featureFlagEnv)
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
}

resource migration 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-migration'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${migrationIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      replicaRetryLimit: 0
      replicaTimeout: 1800
      registries: [
        {
          identity: migrationIdentityId
          server: registryLoginServer
        }
      ]
      triggerType: 'Manual'
    }
    template: {
      containers: [
        {
          name: 'migration'
          image: migrationImage
          args: ['migrate']
          env: commonEnv
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
}

resource exportProcessor 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-exports'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      replicaRetryLimit: 2
      replicaTimeout: 900
      scheduleTriggerConfig: {
        cronExpression: '*/5 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          identity: workerIdentityId
          server: registryLoginServer
        }
      ]
      triggerType: 'Schedule'
    }
    template: {
      containers: [
        {
          name: 'exports'
          image: workerImage
          args: ['exports']
          env: commonEnv
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
}

output apiName string = api.name
output apiFqdn string = api.properties.configuration.ingress.fqdn
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output webName string = web.name
output webUrl string = 'https://${web.properties.configuration.ingress.fqdn}'
output spaRedirectUri string = 'https://${web.properties.configuration.ingress.fqdn}/auth'
output spaLogoutUri string = 'https://${web.properties.configuration.ingress.fqdn}'
output workerName string = worker.name
output schedulerJobName string = enableScheduler ? scheduler.name : ''
output migrationJobName string = migration.name
output exportJobName string = exportProcessor.name
output customDomainRequested string = customDomainName
output certificateResourceId string = !empty(certificateKeyVaultSecretId) ? managedCertificate.id : ''
