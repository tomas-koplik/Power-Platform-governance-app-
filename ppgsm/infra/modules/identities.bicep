param namePrefix string
param location string
param tags object

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-api-id'
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-worker-id'
  location: location
  tags: tags
}

resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-web-id'
  location: location
  tags: tags
}

resource deploymentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-migration-id'
  location: location
  tags: tags
}

resource certificateIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-tls-certificate-id'
  location: location
  tags: tags
}

output apiIdentityId string = apiIdentity.id
output apiPrincipalId string = apiIdentity.properties.principalId
output workerIdentityId string = workerIdentity.id
output workerPrincipalId string = workerIdentity.properties.principalId
output webIdentityId string = webIdentity.id
output webPrincipalId string = webIdentity.properties.principalId
output migrationIdentityId string = deploymentIdentity.id
output migrationPrincipalId string = deploymentIdentity.properties.principalId
output certificateIdentityId string = certificateIdentity.id
output certificatePrincipalId string = certificateIdentity.properties.principalId
