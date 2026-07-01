targetScope = 'subscription'

@description('Azure region for the resource group and resources.')
param location string = 'swedencentral'

@description('Resource group name to create or update.')
param resourceGroupName string = 'partyplaylist'

@description('Environment name used in resource naming and tags.')
param environmentName string = 'prod'

@description('Globally unique Azure Function App name.')
param functionAppName string = 'partyplaylist-ew'

@description('Spotify application client id.')
param spotifyClientId string

@secure()
@description('Spotify application client secret. Stored in Key Vault and referenced by the Function App.')
param spotifyClientSecret string

@description('Spotify redirect URI registered in Spotify Developer Dashboard.')
param spotifyRedirectUri string = 'https://${functionAppName}.azurewebsites.net/api/auth/callback'

@description('Maximum number of Flex Consumption instances.')
param maximumInstanceCount int = 40

@allowed([
  512
  2048
  4096
])
@description('Flex Consumption instance memory in MB.')
param instanceMemoryMB int = 2048

var tags = {
  application: 'partyplaylist'
  environment: environmentName
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module app './resources.bicep' = {
  name: 'partyplaylist-${environmentName}-resources'
  scope: resourceGroup
  params: {
    location: location
    environmentName: environmentName
    functionAppName: functionAppName
    spotifyClientId: spotifyClientId
    spotifyClientSecret: spotifyClientSecret
    spotifyRedirectUri: spotifyRedirectUri
    maximumInstanceCount: maximumInstanceCount
    instanceMemoryMB: instanceMemoryMB
    tags: tags
  }
}

output resourceGroupName string = resourceGroup.name
output functionAppName string = app.outputs.functionAppName
output functionAppUrl string = app.outputs.functionAppUrl
output spotifyRedirectUri string = spotifyRedirectUri
output keyVaultName string = app.outputs.keyVaultName
output storageAccountName string = app.outputs.storageAccountName
output applicationInsightsName string = app.outputs.applicationInsightsName
