targetScope = 'subscription'

@allowed(['dev', 'test', 'staging', 'prod'])
param environmentName string
param location string = 'westeurope'
param resourceGroupName string = 'rg-ppgsm-${environmentName}'
param namePrefix string = 'ppgsm-${environmentName}'
param ownerTag string
param costCenterTag string

var tags = {
  application: 'PPGSM'
  environment: environmentName
  owner: ownerTag
  costCenter: costCenterTag
  dataClassification: 'Confidential'
  managedBy: 'Bicep'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
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
  }
}

output resourceGroupName string = resourceGroup.name
output registryName string = registry.outputs.registryName
output registryLoginServer string = registry.outputs.loginServer
