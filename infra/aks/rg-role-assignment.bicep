targetScope = 'resourceGroup'

@description('Principal ID of the service principal or managed identity.')
param principalId string

@description('Full resource ID of the role definition.')
param roleDefinitionId string

// The name includes the principal ID, so a recreated identity gets a new assignment instead of a RoleAssignmentExists conflict.
resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, roleDefinitionId)
  properties: {
    roleDefinitionId: roleDefinitionId
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
