@description('Azure region for all resources in this resource group.')
param location string = resourceGroup().location

@description('Environment name used in resource naming and tags.')
param environmentName string

@description('Globally unique Azure Function App name.')
param functionAppName string

@description('Spotify application client id.')
param spotifyClientId string

@secure()
@description('Spotify application client secret. Stored in Key Vault and referenced by the Function App.')
param spotifyClientSecret string

@description('Spotify redirect URI registered in Spotify Developer Dashboard.')
param spotifyRedirectUri string

@description('Maximum number of Flex Consumption instances.')
param maximumInstanceCount int

@description('Flex Consumption instance memory in MB.')
param instanceMemoryMB int

@description('Tags applied to resources.')
param tags object = {}

var normalizedFunctionName = replace(toLower(functionAppName), '-', '')
var unique = uniqueString(resourceGroup().id, functionAppName, environmentName)
var storageAccountName = take('st${normalizedFunctionName}${unique}', 24)
var planName = 'plan-${functionAppName}-${environmentName}'
var logAnalyticsName = 'log-${functionAppName}-${environmentName}'
var applicationInsightsName = 'appi-${functionAppName}-${environmentName}'
var keyVaultName = take('kv-${normalizedFunctionName}-${unique}', 24)
var deploymentStorageContainerName = 'function-releases'
var deploymentStorageConnectionStringSettingName = 'DeploymentStorageConnectionString'
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentStorageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource hostingPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: false
    enabledForTemplateDeployment: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    accessPolicies: []
  }
}

resource spotifyClientSecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'spotify-client-secret'
  properties: {
    value: spotifyClientSecret
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: union(tags, {
    'azd-service-name': 'api'
  })
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}${deploymentStorageContainerName}'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: deploymentStorageConnectionStringSettingName
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      alwaysOn: false
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'AzureWebJobsStorage'
          value: storageConnectionString
        }
        {
          name: deploymentStorageConnectionStringSettingName
          value: storageConnectionString
        }
        {
          name: 'Tables__ConnectionString'
          value: storageConnectionString
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsights.properties.ConnectionString
        }
        {
          name: 'Spotify__ClientId'
          value: spotifyClientId
        }
        {
          name: 'Spotify__ClientSecret'
          value: '@Microsoft.KeyVault(SecretUri=${spotifyClientSecretResource.properties.secretUri})'
        }
        {
          name: 'Spotify__RedirectUri'
          value: spotifyRedirectUri
        }
      ]
    }
  }
  dependsOn: [
    deploymentContainer
  ]
}

resource functionAppKeyVaultPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: keyVault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: tenant().tenantId
        objectId: functionApp.identity.principalId
        permissions: {
          secrets: [
            'get'
            'list'
          ]
        }
      }
    ]
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output storageAccountName string = storageAccount.name
output keyVaultName string = keyVault.name
output applicationInsightsName string = applicationInsights.name
