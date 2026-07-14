param namePrefix string
param location string
param tags object
param retentionInDays int = 90
param alertEmail string = ''

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-law'
  location: location
  tags: tags
  properties: {
    retentionInDays: retentionInDays
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
    RetentionInDays: retentionInDays
  }
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${namePrefix}-ops-ag'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'PPGSMOps'
    enabled: true
    emailReceivers: empty(alertEmail) ? [] : [
      {
        name: 'PrimaryOperations'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
    webhookReceivers: []
  }
}

output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
output appInsightsId string = appInsights.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output actionGroupId string = actionGroup.id
