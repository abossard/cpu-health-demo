targetScope = 'subscription'

@description('Azure region for the isolated demo resources.')
param location string = 'swedencentral'

@description('Name of the dedicated resource group.')
param resourceGroupName string = 'rg-cpu-health-demo'

@description('Administrator account required by the Linux VM resource. Inbound SSH is not allowed.')
param adminUsername string = 'cpuadmin'

@secure()
@description('Bootstrap password required by the VM API. It is not stored in this source tree.')
param adminPassword string

param vmNames array = [
  'cpu-health-demo-1'
  'cpu-health-demo-2'
  'cpu-health-demo-3'
]

resource demoResourceGroup 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: resourceGroupName
  location: location
  tags: {
    project: 'cpu-health-demo'
    purpose: 'cpu-health-demo'
  }
}

module compute './compute.bicep' = {
  name: 'cpu-health-demo-compute'
  scope: demoResourceGroup
  params: {
    location: location
    vmNames: vmNames
    adminUsername: adminUsername
    adminPassword: adminPassword
  }
}

output resourceGroupName string = demoResourceGroup.name
output vmResourceIds array = compute.outputs.vmResourceIds
