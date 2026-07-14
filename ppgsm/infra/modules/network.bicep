param namePrefix string
param location string
param tags object
param addressSpace string = '10.40.0.0/16'
param infrastructureSubnetPrefix string = '10.40.0.0/23'
param privateEndpointSubnetPrefix string = '10.40.2.0/24'

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${namePrefix}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [addressSpace]
    }
    subnets: [
      {
        name: 'container-apps-infrastructure'
        properties: {
          addressPrefix: infrastructureSubnetPrefix
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource infrastructureSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'container-apps-infrastructure'
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'private-endpoints'
}

output virtualNetworkId string = vnet.id
output infrastructureSubnetId string = infrastructureSubnet.id
output privateEndpointSubnetId string = privateEndpointSubnet.id
