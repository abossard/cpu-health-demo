targetScope = 'resourceGroup'

param location string
param vmNames array
param adminUsername string
@secure()
param adminPassword string

var addressPrefix = '10.42.0.0/16'
var subnetPrefix = '10.42.1.0/24'
var runnerScript = base64(loadTextContent('../stress/run.sh'))

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-cpu-health-demo'
  location: location
  properties: {
    securityRules: []
  }
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: 'vnet-cpu-health-demo'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        addressPrefix
      ]
    }
    subnets: [
      {
        name: 'demo'
        properties: {
          addressPrefix: subnetPrefix
          networkSecurityGroup: {
            id: networkSecurityGroup.id
          }
        }
      }
    ]
  }
}

resource publicIps 'Microsoft.Network/publicIPAddresses@2023-11-01' = [for (vmName, i) in vmNames: {
  name: 'pip-${vmName}'
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
    idleTimeoutInMinutes: 4
  }
}]

resource networkInterfaces 'Microsoft.Network/networkInterfaces@2023-11-01' = [for (vmName, i) in vmNames: {
  name: 'nic-${vmName}'
  location: location
  properties: {
    networkSecurityGroup: {
      id: networkSecurityGroup.id
    }
    ipConfigurations: [
      {
        name: 'primary'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: {
            id: resourceId('Microsoft.Network/virtualNetworks/subnets', virtualNetwork.name, 'demo')
          }
          publicIPAddress: {
            id: publicIps[i].id
          }
        }
      }
    ]
  }
}]

resource virtualMachines 'Microsoft.Compute/virtualMachines@2024-03-01' = [for (vmName, i) in vmNames: {
  name: vmName
  location: location
  tags: {
    project: 'cpu-health-demo'
    purpose: 'cpu-health-demo'
  }
  properties: {
    hardwareProfile: {
      vmSize: 'Standard_D2s_v5'
    }
    osProfile: {
      computerName: vmName
      adminUsername: adminUsername
      adminPassword: adminPassword
      customData: base64(replace(loadTextContent('cloud-init.yaml'), '__RUNNER_BASE64__', runnerScript))
      linuxConfiguration: {
        disablePasswordAuthentication: false
        provisionVMAgent: true
      }
    }
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: '0001-com-ubuntu-server-jammy'
        sku: '22_04-lts-gen2'
        version: 'latest'
      }
      osDisk: {
        createOption: 'FromImage'
        managedDisk: {
          storageAccountType: 'Standard_LRS'
        }
      }
    }
    networkProfile: {
      networkInterfaces: [
        {
          id: networkInterfaces[i].id
          properties: {
            primary: true
          }
        }
      ]
    }
  }
}]

output vmResourceIds array = [for (vmName, i) in vmNames: virtualMachines[i].id]
