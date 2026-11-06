targetScope = 'resourceGroup'

@minLength(3)
@maxLength(44)
param healthModelName string = 'cpu-health-demo'

@description('Azure region where Microsoft.CloudHealth is available.')
param location string = 'swedencentral'

@description('Exactly three deployed VM names in this resource group.')
param vmNames array = [
  'cpu-health-demo-1'
  'cpu-health-demo-2'
  'cpu-health-demo-3'
]

@description('Resource IDs returned by the approved infrastructure deployment.')
param vmResourceIds array

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var flowEntityName = 'observe-cpu-pressure'

resource healthModel 'Microsoft.CloudHealth/healthmodels@2026-11-01' = {
  name: healthModelName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

resource modelReaderAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (vmName, i) in vmNames: {
  name: guid(monitoredVms[i].id, healthModel.id, readerRoleId)
  scope: monitoredVms[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: healthModel.identity.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource auth 'Microsoft.CloudHealth/healthmodels/authenticationsettings@2026-11-01' = {
  parent: healthModel
  name: 'system-assigned'
  properties: {
    authenticationKind: 'ManagedIdentity'
    managedIdentityName: 'SystemAssigned'
  }
  dependsOn: [
    modelReaderAssignments
  ]
}

resource cpuDefinition 'Microsoft.CloudHealth/healthmodels/signaldefinitions@2026-11-01' = {
  parent: healthModel
  name: 'average-cpu'
  properties: {
    signalKind: 'AzureResourceMetric'
    displayName: 'Average CPU utilization'
    metricNamespace: 'Microsoft.Compute/virtualMachines'
    metricName: 'Percentage CPU'
    aggregationType: 'Average'
    timeGrain: 'PT5M'
    refreshInterval: 'PT1M'
    dataUnit: 'Percent'
    evaluationRules: {
      degradedRule: {
        operator: 'GreaterThan'
        threshold: 90
      }
      unhealthyRule: {
        operator: 'GreaterThan'
        threshold: 98
      }
    }
  }
}

resource cpuPressureFlow 'Microsoft.CloudHealth/healthmodels/entities@2026-11-01' = {
  parent: healthModel
  name: flowEntityName
  properties: {
    displayName: 'Observe per-VM CPU pressure'
    icon: {
      iconName: 'UserFlow'
    }
    signalGroups: {
      dependencies: {
        aggregationType: 'WorstOf'
      }
    }
  }
}

resource monitoredVms 'Microsoft.Compute/virtualMachines@2024-03-01' existing = [for vmName in vmNames: {
  name: vmName
}]

resource vmEntities 'Microsoft.CloudHealth/healthmodels/entities@2026-11-01' = [for (vmName, i) in vmNames: {
  parent: healthModel
  name: vmName
  properties: {
    displayName: 'Virtual machine: ${vmName}'
    signalGroups: {
      azureResource: {
        authenticationSetting: auth.name
        azureResourceId: vmResourceIds[i]
        signals: [
          {
            name: 'average-cpu'
            signalKind: 'AzureResourceMetric'
            signalDefinitionName: cpuDefinition.name
          }
        ]
      }
    }
  }
}]

resource rootRelationship 'Microsoft.CloudHealth/healthmodels/relationships@2026-11-01' = {
  parent: healthModel
  name: '${healthModelName}-${flowEntityName}'
  properties: {
    parentEntityName: healthModelName
    childEntityName: flowEntityName
  }
  dependsOn: [
    cpuPressureFlow
  ]
}

resource vmRelationships 'Microsoft.CloudHealth/healthmodels/relationships@2026-11-01' = [for vmName in vmNames: {
  parent: healthModel
  name: '${flowEntityName}-${vmName}'
  properties: {
    parentEntityName: flowEntityName
    childEntityName: vmName
  }
  dependsOn: [
    rootRelationship
    vmEntities
  ]
}]

output healthModelId string = healthModel.id
output healthModelPrincipalId string = healthModel.identity.principalId
output monitoredVmIds array = vmResourceIds
