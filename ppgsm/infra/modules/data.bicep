param namePrefix string
param location string
param tags object
param privateEndpointSubnetId string
param virtualNetworkId string
param workspaceId string
param apiPrincipalId string
param workerPrincipalId string
param migrationPrincipalId string
param certificatePrincipalId string
param apiRawEvidenceRoleId string
param sqlEntraAdminLogin string
param sqlEntraAdminObjectId string
param tenantId string
param sqlSkuName string = 'GP_S_Gen5_1'
param sqlMaxSizeBytes int = 34359738368
param sqlZoneRedundant bool = false
param serviceBusSku string = 'Standard'
param rawEvidenceRetentionDays int = 30
param immutableRawEvidence bool = false
param exportRetentionDays int = 30

var storageName = take(replace('${namePrefix}data${uniqueString(resourceGroup().id)}', '-', ''), 24)
var sqlServerName = take('${namePrefix}-sql-${uniqueString(resourceGroup().id)}', 63)
var serviceBusName = take('${namePrefix}-sb-${uniqueString(resourceGroup().id)}', 50)
var daemonKeyVaultName = take('${namePrefix}-daemon-kv-${uniqueString(resourceGroup().id)}', 24)
var blobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var blobDataReaderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
var blobDelegatorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a')
var serviceBusDataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var serviceBusDataReceiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6c51bf-1d5b-418c-aeff-b845f5f3e08e')
var keyVaultSecretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var diagnosticCategories = [
  'AuditEvent'
]

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_ZRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 14
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 14
    }
    isVersioningEnabled: true
  }
}

resource rawSnapshots 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'raw-snapshots'
  properties: {
    publicAccess: 'None'
    immutableStorageWithVersioning: {
      enabled: immutableRawEvidence
    }
  }
}

resource exports 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'exports'
  properties: {
    publicAccess: 'None'
  }
}

resource rawSnapshotsImmutability 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = if (immutableRawEvidence) {
  parent: rawSnapshots
  name: 'default'
  properties: {
    immutabilityPeriodSinceCreationInDays: rawEvidenceRetentionDays
    allowProtectedAppendWrites: false
  }
}

resource storageLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          enabled: true
          name: 'raw-evidence-retention'
          type: 'Lifecycle'
          definition: {
            actions: {
              baseBlob: {
                tierToCool: {
                  daysAfterModificationGreaterThan: 30
                }
                delete: {
                  daysAfterModificationGreaterThan: rawEvidenceRetentionDays
                }
              }
              snapshot: {
                delete: {
                  daysAfterCreationGreaterThan: rawEvidenceRetentionDays
                }
              }
              version: {
                delete: {
                  daysAfterCreationGreaterThan: rawEvidenceRetentionDays
                }
              }
            }
            filters: {
              blobTypes: ['blockBlob']
              prefixMatch: ['raw-snapshots/']
            }
          }
        }
        {
          enabled: true
          name: 'export-retention'
          type: 'Lifecycle'
          definition: {
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: exportRetentionDays
                }
              }
            }
            filters: {
              blobTypes: ['blockBlob']
              prefixMatch: ['exports/']
            }
          }
        }
      ]
    }
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    name: serviceBusSku
    tier: serviceBusSku
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    zoneRedundant: serviceBusSku == 'Premium'
  }
}

resource snapshotQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'snapshot-jobs'
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    requiresDuplicateDetection: true
  }
}

resource daemonKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: daemonKeyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enablePurgeProtection: true
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlEntraAdminLogin
      principalType: 'Group'
      sid: sqlEntraAdminObjectId
      tenantId: tenantId
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    restrictOutboundNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'ppgsm'
  location: location
  tags: tags
  sku: {
    name: sqlSkuName
  }
  properties: {
    autoPauseDelay: contains(sqlSkuName, '_S_') ? 60 : -1
    availabilityZone: sqlZoneRedundant ? '1' : 'NoPreference'
    maxSizeBytes: sqlMaxSizeBytes
    minCapacity: contains(sqlSkuName, '_S_') ? json('0.5') : null
    readScale: 'Disabled'
    zoneRedundant: sqlZoneRedundant
    requestedBackupStorageRedundancy: sqlZoneRedundant ? 'Zone' : 'Local'
  }
}

resource shortTermRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01-preview' = {
  parent: database
  name: 'default'
  properties: {
    retentionDays: 14
    diffBackupIntervalInHours: 24
  }
}

resource longTermRetention 'Microsoft.Sql/servers/databases/backupLongTermRetentionPolicies@2023-08-01-preview' = {
  parent: database
  name: 'default'
  properties: {
    weeklyRetention: 'P4W'
    monthlyRetention: 'P12M'
    yearlyRetention: 'P0Y'
    weekOfYear: 1
  }
}

var privateLinkServices = [
  {
    key: 'blob'
    resourceId: storage.id
    groupId: 'blob'
    zoneName: 'privatelink.blob.core.windows.net'
  }
  {
    key: 'servicebus'
    resourceId: serviceBus.id
    groupId: 'namespace'
    zoneName: 'privatelink.servicebus.windows.net'
  }
  {
    key: 'vault'
    resourceId: daemonKeyVault.id
    groupId: 'vault'
    zoneName: 'privatelink.vaultcore.azure.net'
  }
  {
    key: 'sql'
    resourceId: sqlServer.id
    groupId: 'sqlServer'
    zoneName: 'privatelink.database.windows.net'
  }
]

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for service in privateLinkServices: {
  name: service.zoneName
  location: 'global'
  tags: tags
}]

resource privateDnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (service, index) in privateLinkServices: {
  parent: privateDnsZones[index]
  name: '${namePrefix}-${service.key}-vnet-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetworkId
    }
  }
}]

resource privateEndpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [for service in privateLinkServices: {
  name: '${namePrefix}-${service.key}-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${service.key}-connection'
        properties: {
          groupIds: [service.groupId]
          privateLinkServiceId: service.resourceId
          privateLinkServiceConnectionState: {
            status: 'Approved'
            description: 'Deployed by PPGSM infrastructure'
            actionsRequired: 'None'
          }
        }
      }
    ]
  }
}]

resource privateDnsZoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [for (service, index) in privateLinkServices: {
  parent: privateEndpoints[index]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: service.key
        properties: {
          privateDnsZoneId: privateDnsZones[index].id
        }
      }
    ]
  }
}]

resource storageDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: storage
  properties: {
    workspaceId: workspaceId
    metrics: [
      {
        category: 'Transaction'
        enabled: true
      }
    ]
  }
}

resource serviceBusDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: serviceBus
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource keyVaultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: daemonKeyVault
  properties: {
    workspaceId: workspaceId
    logs: [for category in diagnosticCategories: {
      category: category
      enabled: true
    }]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource sqlDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: database
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource workerRawBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(rawSnapshots.id, workerPrincipalId, blobDataContributorRoleId)
  scope: rawSnapshots
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDataContributorRoleId
  }
}

resource apiRawBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(rawSnapshots.id, apiPrincipalId, apiRawEvidenceRoleId)
  scope: rawSnapshots
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: apiRawEvidenceRoleId
  }
}

resource apiExportsBlobReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(exports.id, apiPrincipalId, blobDataReaderRoleId)
  scope: exports
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDataReaderRoleId
  }
}

resource apiBlobDelegatorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, apiPrincipalId, blobDelegatorRoleId)
  scope: storage
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDelegatorRoleId
  }
}

resource workerExportsBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(exports.id, workerPrincipalId, blobDataContributorRoleId)
  scope: exports
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDataContributorRoleId
  }
}

resource apiBusSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, apiPrincipalId, serviceBusDataSenderRoleId)
  scope: serviceBus
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceBusDataSenderRoleId
  }
}

resource workerBusReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, workerPrincipalId, serviceBusDataReceiverRoleId)
  scope: serviceBus
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceBusDataReceiverRoleId
  }
}

resource workerKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(daemonKeyVault.id, workerPrincipalId, keyVaultSecretsUserRoleId)
  scope: daemonKeyVault
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource apiKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(daemonKeyVault.id, apiPrincipalId, keyVaultSecretsUserRoleId)
  scope: daemonKeyVault
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource certificateKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(daemonKeyVault.id, certificatePrincipalId, keyVaultSecretsUserRoleId)
  scope: daemonKeyVault
  properties: {
    principalId: certificatePrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

output storageAccountName string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output serviceBusNamespace string = serviceBus.name
output serviceBusFqdn string = '${serviceBus.name}.servicebus.windows.net'
output snapshotQueueName string = snapshotQueue.name
output daemonKeyVaultName string = daemonKeyVault.name
output daemonKeyVaultUri string = daemonKeyVault.properties.vaultUri
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = database.name
