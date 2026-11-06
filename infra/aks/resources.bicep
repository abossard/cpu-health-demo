targetScope = 'resourceGroup'

param environmentName string
param location string
param clusterName string
param nodeResourceGroupName string
param principalId string

var systemNodeVmSize = 'Standard_B2s_v2'
var stressNodeVmSize = 'Standard_D2s_v5'
var serviceAccountSubject = 'system:serviceaccount:stress-manager:stress-manager'

var roleIds = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  acrPush: '8311e382-0749-4cb8-b61a-304f252e45ec'
  aksRbacClusterAdmin: 'b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b'
  managedIdentityOperator: 'f1a07417-d97a-45cb-824c-7a7467783830'
}

resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${environmentName}'
  location: location
}

// Stress Job pods only write data annotations, so they get their own identity.
resource runnerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${environmentName}-runner'
  location: location
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: 'cr${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

resource cluster 'Microsoft.ContainerService/managedClusters@2026-05-01' = {
  name: clusterName
  location: location
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: clusterName
    nodeResourceGroup: nodeResourceGroupName
    enableRBAC: true
    aadProfile: {
      managed: true
      enableAzureRBAC: true
      tenantID: tenant().tenantId
    }
    disableLocalAccounts: true
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        type: 'VirtualMachineScaleSets'
        vmSize: systemNodeVmSize
        count: 1
        enableAutoScaling: false
        osType: 'Linux'
        osSKU: 'AzureLinux'
        upgradeSettings: {
          maxSurge: '1'
        }
      }
      {
        name: 'stress'
        mode: 'User'
        type: 'VirtualMachineScaleSets'
        vmSize: stressNodeVmSize
        count: 3
        enableAutoScaling: false
        osType: 'Linux'
        osSKU: 'AzureLinux'
        nodeLabels: {
          workload: 'stress'
        }
        nodeTaints: [
          'workload=stress:NoSchedule'
        ]
        upgradeSettings: {
          maxSurge: '1'
        }
      }
    ]
    networkProfile: {
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      networkDataplane: 'cilium'
      networkPolicy: 'cilium'
      loadBalancerSku: 'standard'
    }
    nodeProvisioningProfile: {
      mode: 'Auto'
    }
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    ingressProfile: {
      webAppRouting: {
        enabled: true
      }
    }
  }
}

// Node image upgrades drain nodes; keep them out of demo hours.
resource nodeOsMaintenance 'Microsoft.ContainerService/managedClusters/maintenanceConfigurations@2026-05-01' = {
  parent: cluster
  name: 'aksManagedNodeOSUpgradeSchedule'
  properties: {
    maintenanceWindow: {
      schedule: {
        weekly: {
          dayOfWeek: 'Sunday'
          intervalWeeks: 1
        }
      }
      startTime: '03:00'
      utcOffset: '+00:00'
      durationHours: 4
    }
  }
}

resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: managedIdentity
  name: 'stress-manager'
  properties: {
    issuer: cluster.properties.oidcIssuerProfile.issuerURL
    subject: serviceAccountSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource runnerFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: runnerIdentity
  name: 'stress-runner'
  properties: {
    issuer: cluster.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:stress-manager:stress-runner'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource managerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'stress-manager-health-model')
  properties: {
    roleName: 'Stress manager health model (${resourceGroup().name})'
    description: 'Builds and reconciles the CPU health model and writes backstop data annotations.'
    type: 'CustomRole'
    assignableScopes: [
      resourceGroup().id
    ]
    permissions: [
      {
        actions: [
          'Microsoft.CloudHealth/healthmodels/read'
          'Microsoft.CloudHealth/healthmodels/write'
          'Microsoft.CloudHealth/healthmodels/authenticationsettings/read'
          'Microsoft.CloudHealth/healthmodels/authenticationsettings/write'
          'Microsoft.CloudHealth/healthmodels/signaldefinitions/read'
          'Microsoft.CloudHealth/healthmodels/signaldefinitions/write'
          'Microsoft.CloudHealth/healthmodels/entities/read'
          'Microsoft.CloudHealth/healthmodels/entities/write'
          'Microsoft.CloudHealth/healthmodels/entities/delete'
          'Microsoft.CloudHealth/healthmodels/entities/getHistory/action'
          'Microsoft.CloudHealth/healthmodels/entities/getSignalHistory/action'
          'Microsoft.CloudHealth/healthmodels/entities/getDataAnnotations/action'
          'Microsoft.CloudHealth/healthmodels/entities/addDataAnnotation/action'
          'Microsoft.CloudHealth/healthmodels/relationships/read'
          'Microsoft.CloudHealth/healthmodels/relationships/write'
          'Microsoft.CloudHealth/healthmodels/relationships/delete'
          'Microsoft.CloudHealth/Locations/operationstatuses/read'
          'Microsoft.ManagedIdentity/userAssignedIdentities/assign/action'
        ]
      }
    ]
  }
}

resource runnerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'stress-runner-annotations')
  properties: {
    roleName: 'Stress runner annotations (${resourceGroup().name})'
    description: 'Writes and reads start/end data annotations on health model entities.'
    type: 'CustomRole'
    assignableScopes: [
      resourceGroup().id
    ]
    permissions: [
      {
        actions: [
          'Microsoft.CloudHealth/healthmodels/entities/read'
          'Microsoft.CloudHealth/healthmodels/entities/addDataAnnotation/action'
          'Microsoft.CloudHealth/healthmodels/entities/getDataAnnotations/action'
        ]
      }
    ]
  }
}

resource kubeletAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, clusterName, roleIds.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.acrPull)
    principalId: cluster.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

resource userAcrPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(registry.id, principalId, roleIds.acrPush)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.acrPush)
    principalId: principalId
  }
}

// Local accounts are off, so azd and kubectl reach the API server through kubelogin with this role.
resource userClusterAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(cluster.id, principalId, roleIds.aksRbacClusterAdmin)
  scope: cluster
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.aksRbacClusterAdmin)
    principalId: principalId
  }
}

module managerRoleAssignment './rg-role-assignment.bicep' = {
  name: 'stress-manager-role-assignment'
  params: {
    principalId: managedIdentity.properties.principalId
    roleDefinitionId: managerRole.id
  }
}

module runnerRoleAssignment './rg-role-assignment.bicep' = {
  name: 'stress-runner-role-assignment'
  params: {
    principalId: runnerIdentity.properties.principalId
    roleDefinitionId: runnerRole.id
  }
}

resource identityOperatorOnSelf 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(managedIdentity.id, roleIds.managedIdentityOperator)
  scope: managedIdentity
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.managedIdentityOperator)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output clusterName string = cluster.name
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output managedIdentityId string = managedIdentity.id
output managedIdentityClientId string = managedIdentity.properties.clientId
output managedIdentityPrincipalId string = managedIdentity.properties.principalId
output runnerClientId string = runnerIdentity.properties.clientId
