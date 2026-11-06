targetScope = 'subscription'

@minLength(1)
// 34 keeps the node RG name (MC_rg-<env>_aks-<env>) within 80 characters.
@maxLength(34)
@description('azd environment name. Drives all resource names.')
param environmentName string

@description('Azure region for all resources and the health model.')
param location string = 'swedencentral'

@description('Object ID of the deploying user. Gets AcrPush on the registry and RBAC Cluster Admin on AKS when set.')
param principalId string = ''

var resourceGroupName = 'rg-${environmentName}'
var clusterName = 'aks-${environmentName}'
var nodeResourceGroupName = 'MC_${resourceGroupName}_${clusterName}'

resource resourceGroup 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: resourceGroupName
  location: location
  tags: {
    'azd-env-name': environmentName
    project: 'cpu-health-demo'
  }
}

module resources './resources.bicep' = {
  name: 'aks-resources'
  scope: resourceGroup
  params: {
    environmentName: environmentName
    location: location
    clusterName: clusterName
    nodeResourceGroupName: nodeResourceGroupName
    principalId: principalId
  }
}

module nodeResourceGroupReader './node-rg-reader.bicep' = {
  name: 'aks-node-rg-reader'
  scope: az.resourceGroup(nodeResourceGroupName)
  params: {
    principalId: resources.outputs.managedIdentityPrincipalId
  }
}

output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_AKS_CLUSTER_NAME string = resources.outputs.clusterName
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.registryLoginServer
output AZURE_CONTAINER_REGISTRY_NAME string = resources.outputs.registryName
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_SUBSCRIPTION_ID string = subscription().subscriptionId
output AZURE_MANAGED_IDENTITY_CLIENT_ID string = resources.outputs.managedIdentityClientId
output AZURE_MANAGED_IDENTITY_ID string = resources.outputs.managedIdentityId
output AZURE_RUNNER_CLIENT_ID string = resources.outputs.runnerClientId
output AZURE_NODE_RESOURCE_GROUP string = nodeResourceGroupName
output HEALTH_MODEL_NAME string = 'hm-${environmentName}'
output HEALTH_MODEL_LOCATION string = location
