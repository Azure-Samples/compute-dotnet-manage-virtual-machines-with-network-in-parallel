// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Samples.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Network;
using Azure;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;

namespace ManageVirtualMachinesInParallelWithNetwork
{
    public class Program
    {
        private const int FrontendVMCount = 10;
        private const int BackendVMCount = 10;
        private static readonly string UserName = Utilities.CreateUsername();
        private static readonly string Password = Utilities.CreatePassword();

        /**
         * Create a virtual network with two Subnets ?frontend and backend
         * Frontend allows HTTP in and denies Internet out
         * Backend denies Internet in and Internet out
         * Create m Linux virtual machines in the frontend
         * Create m Windows virtual machines in the backend.
         */
        public static async Task RunSample(ArmClient client)
        {
            string rgName = Utilities.CreateRandomName("rgNEPP");
            string frontEndNSGName = Utilities.CreateRandomName("fensg");
            string backEndNSGName = Utilities.CreateRandomName("bensg");
            string networkName = Utilities.CreateRandomName("vnetCOMV");
            string pipName = Utilities.CreateRandomName("pip1");
            string linuxComputerName = Utilities.CreateRandomName("linuxComputer");
            string networkConfigurationName = Utilities.CreateRandomName("networkconfiguration");
            string storageAccountName = Utilities.CreateRandomName("stgCOMV");
            string storageAccountSkuName = Utilities.CreateRandomName("stgSku");
            var pipDnsLabelLinuxVM = Utilities.CreateRandomName("rgpip1");
            var region = AzureLocation.EastUS;
            // Create a resource group [Where all resources gets created]
            var lro = await client.GetDefaultSubscription().GetResourceGroups().CreateOrUpdateAsync(Azure.WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
            var resourceGroup = lro.Value;
            try
            {
                // Define a network security group for the front end of a subnet
                // front end subnet contains two rules
                // - ALLOW-SSH - allows SSH traffic into the front end subnet
                // - ALLOW-WEB- allows HTTP traffic into the front end subnet

                var frontEndNSGCollection = resourceGroup.GetNetworkSecurityGroups();
                var frontEndNSGData = new NetworkSecurityGroupData()
                {
                    Location = region,
                    SecurityRules =
                    {
                        new SecurityRuleData()
                        {
                            Priority = 100,
                            Description = "Allow SSH",
                            Protocol = "tcp",
                            SourcePortRange = "22"
                        },
                        new SecurityRuleData()
                        {
                            Priority = 101,
                            Description = "ALLOW-HTTP",
                            Protocol = "tcp",
                            SourcePortRange = "80"
                        }
                    }
                };
                var frontEndNSGCreatable = (await frontEndNSGCollection.CreateOrUpdateAsync(WaitUntil.Completed, frontEndNSGName, frontEndNSGData)).Value;

                //============================================================
                // Define a network security group for the back end of a subnet
                // back end subnet contains two rules
                // - ALLOW-SQL - allows SQL traffic only from the front end subnet
                // - DENY-WEB - denies all outbound internet traffic from the back end subnet
                var backEndNSGCollection = resourceGroup.GetNetworkSecurityGroups();
                var backEndNSGData = new NetworkSecurityGroupData()
                {
                    Location = region,
                    SecurityRules =
                    {
                        new SecurityRuleData()
                        {
                            Priority = 100,
                            Description = "ALLOW-SQL",
                            Protocol = "tcp",
                            SourcePortRange = "1433",
                            SourceAddressPrefix = "172.16.1.0/24"
                        },
                        new SecurityRuleData()
                        {
                            Priority = 200,
                            Description = "DENY-WEB",
                        }
                    }
                };
                var backEndNSGCreatable = (await frontEndNSGCollection.CreateOrUpdateAsync(WaitUntil.Completed, frontEndNSGName, frontEndNSGData)).Value;

                Utilities.Log("Creating a security group for the front ends - allows SSH and HTTP");
                Utilities.Log("Creating a security group for the back ends - allows SSH and denies all outbound internet traffic");

                var networkSecurityGroups = new List<NetworkSecurityGroupResource>
                {
                    frontEndNSGCreatable,
                    backEndNSGCreatable
                };

                NetworkSecurityGroupResource frontendNSG = networkSecurityGroups.First(n => n.Data.Name.Equals(frontEndNSGName, StringComparison.OrdinalIgnoreCase));
                NetworkSecurityGroupResource backendNSG = networkSecurityGroups.First(n => n.Data.Name.Equals(backEndNSGName, StringComparison.OrdinalIgnoreCase));

                Utilities.Log("Created a security group for the front end: " + frontendNSG.Id);
                Utilities.PrintNetworkSecurityGroup(frontendNSG);

                Utilities.Log("Created a security group for the back end: " + backendNSG.Id);
                Utilities.PrintNetworkSecurityGroup(backendNSG);

                // Create Network [Where all the virtual machines get added to]
                var virtualNetworkCollection = resourceGroup.GetVirtualNetworks();
                var data = new VirtualNetworkData()
                {
                    Location = AzureLocation.EastUS,
                    Subnets =
                    {
                        new SubnetData()
                        {
                            AddressPrefixes =
                            {
                                "172.16.1.0/24"
                            },
                            Name = "Front-end"
                        },
                        new SubnetData()
                        {
                            AddressPrefixes =
                            {
                                "172.16.2.0/24"
                            },
                            Name = "Back-end"
                        }
                    },
                    AddressPrefixes =
                    {
                        "172.16.0.0/16"
                    }
                };
                var virtualNetworkLro = await virtualNetworkCollection.CreateOrUpdateAsync(WaitUntil.Completed, networkName, data);
                var virtualNetwork = virtualNetworkLro.Value;
                // Create a public IP address
                Utilities.Log("Creating a Linux Public IP address...");
                var publicAddressIPCollection = resourceGroup.GetPublicIPAddresses();
                var publicIPAddressdata = new PublicIPAddressData()
                {
                    Location = AzureLocation.EastUS,
                    Sku = new PublicIPAddressSku()
                    {
                        Name = PublicIPAddressSkuName.Standard,
                    },
                    PublicIPAddressVersion = NetworkIPVersion.IPv4,
                    PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
                    DnsSettings = new PublicIPAddressDnsSettings()
                    {
                        DomainNameLabel = pipDnsLabelLinuxVM
                    },
                };
                var publicIPAddressLro = await publicAddressIPCollection.CreateOrUpdateAsync(WaitUntil.Completed, pipName, publicIPAddressdata);
                var publicIPAddress = publicIPAddressLro.Value;
                Utilities.Log("Created a Linux Public IP address with name : " + publicIPAddress.Data.Name);

                //Create a networkInterface
                Utilities.Log("Created  linux networkInterfaces");
                var frontEndNetworkInterfaceData = new NetworkInterfaceData()
                {
                    Location = AzureLocation.EastUS,
                    IPConfigurations =
                    {
                        new NetworkInterfaceIPConfigurationData()
                        {
                            Name = "internal",
                            Primary = true,
                            Subnet = new SubnetData
                            {
                                Name = "Front-end",
                                Id = new ResourceIdentifier($"{virtualNetwork.Data.Id}/subnets/Front-end")
                            },
                            PrivateIPAllocationMethod = NetworkIPAllocationMethod.Dynamic,
                            PublicIPAddress = publicIPAddress.Data,
                        }
                    }
                };
                var frontEndNetworkInterfaceName = Utilities.CreateRandomName("frontendnetworkInterface");
                var frontEndNic = (await resourceGroup.GetNetworkInterfaces().CreateOrUpdateAsync(WaitUntil.Completed, frontEndNetworkInterfaceName, frontEndNetworkInterfaceData)).Value;
                Utilities.Log("Created a Linux network interface with name : " + frontEndNic.Data.Name);

                var backEndNetworkInterfaceData = new NetworkInterfaceData()
                {
                    Location = AzureLocation.EastUS,
                    IPConfigurations =
                    {
                        new NetworkInterfaceIPConfigurationData()
                        {
                            Name = "internal",
                            Primary = true,
                            Subnet = new SubnetData
                            {
                                Name = "Back-end",
                                Id = new ResourceIdentifier($"{virtualNetwork.Data.Id}/subnets/Front-end")
                            },
                            PrivateIPAllocationMethod = NetworkIPAllocationMethod.Dynamic,
                            PublicIPAddress = publicIPAddress.Data,
                        }
                    }
                };
                var backEndnetworkInterfaceName = Utilities.CreateRandomName("backendnetworkInterface");
                var backEndNic = (await resourceGroup.GetNetworkInterfaces().CreateOrUpdateAsync(WaitUntil.Completed, backEndnetworkInterfaceName, backEndNetworkInterfaceData)).Value;
                Utilities.Log("Created a Linux network interface with name : " + backEndNic.Data.Name);

                // Prepare Creatable Storage account definition [For storing VMs disk]
                var storageAccountCollection = resourceGroup.GetStorageAccounts();
                var acountData = new StorageAccountCreateOrUpdateContent(new StorageSku(storageAccountSkuName), StorageKind.Storage, region);
                var creatableStorageAccount = (await storageAccountCollection.CreateOrUpdateAsync(WaitUntil.Completed, storageAccountName, acountData)).Value;

                // Prepare a batch of Creatable Virtual Machines definitions
                var virtualMachineCollection = resourceGroup.GetVirtualMachines();
                List<VirtualMachineResource> frontendCreatableVirtualMachines = new List<VirtualMachineResource>();

                for (int i = 0; i < FrontendVMCount; i++)
                {
                    var backEndVmdata = new VirtualMachineData(AzureLocation.EastUS)
                    {
                        HardwareProfile = new VirtualMachineHardwareProfile()
                        {
                            VmSize = "Standard_D2a_v4"
                        },
                        OSProfile = new VirtualMachineOSProfile()
                        {
                            AdminUsername = UserName,
                            AdminPassword = Password,
                            ComputerName = linuxComputerName,
                        },
                        NetworkProfile = new VirtualMachineNetworkProfile()
                        {
                            NetworkInterfaces =
                        {
                            new VirtualMachineNetworkInterfaceReference()
                            {
                                Id = frontEndNic.Id,
                                Primary = true,
                            }
                        }
                        },
                        StorageProfile = new VirtualMachineStorageProfile()
                        {
                            OSDisk = new VirtualMachineOSDisk(DiskCreateOptionType.FromImage)
                            {
                                OSType = SupportedOperatingSystemType.Linux,
                                Caching = CachingType.ReadWrite,
                                ManagedDisk = new VirtualMachineManagedDisk()
                                {
                                    StorageAccountType = StorageAccountType.StandardLrs
                                }
                            },
                            ImageReference = new ImageReference()
                            {
                                Publisher = "Canonical",
                                Offer = "UbuntuServer",
                                Sku = "16.04-LTS",
                                Version = "latest",
                            }
                        },
                    };
                    var virtualMachine_lro = await virtualMachineCollection.CreateOrUpdateAsync(WaitUntil.Completed, "VM-FE-" + i, backEndVmdata);
                    var virtualMachine = virtualMachine_lro.Value;
                    frontendCreatableVirtualMachines.Add(virtualMachine);
                }

                List<VirtualMachineResource> backendCreatableVirtualMachines = new List<VirtualMachineResource>();

                for (int i = 0; i < BackendVMCount; i++)
                {
                    var backEndVmdata = new VirtualMachineData(AzureLocation.EastUS)
                    {
                        HardwareProfile = new VirtualMachineHardwareProfile()
                        {
                            VmSize = "Standard_D2a_v4"
                        },
                        OSProfile = new VirtualMachineOSProfile()
                        {
                            AdminUsername = UserName,
                            AdminPassword = Password,
                            ComputerName = linuxComputerName,
                        },
                        NetworkProfile = new VirtualMachineNetworkProfile()
                        {
                            NetworkInterfaces =
                        {
                            new VirtualMachineNetworkInterfaceReference()
                            {
                                Id = backEndNic.Id,
                                Primary = true,
                            }
                        }
                        },
                        StorageProfile = new VirtualMachineStorageProfile()
                        {
                            OSDisk = new VirtualMachineOSDisk(DiskCreateOptionType.FromImage)
                            {
                                OSType = SupportedOperatingSystemType.Linux,
                                Caching = CachingType.ReadWrite,
                                ManagedDisk = new VirtualMachineManagedDisk()
                                {
                                    StorageAccountType = StorageAccountType.StandardLrs
                                }
                            },
                            ImageReference = new ImageReference()
                            {
                                Publisher = "Canonical",
                                Offer = "UbuntuServer",
                                Sku = "16.04-LTS",
                                Version = "latest",
                            }
                        },
                    };
                    var virtualMachine_lro = await virtualMachineCollection.CreateOrUpdateAsync(WaitUntil.Completed, "VM-FE-" + i, backEndVmdata);
                    var virtualMachine = virtualMachine_lro.Value;
                    backendCreatableVirtualMachines.Add(virtualMachine);
                }

                var startTime = DateTimeOffset.Now.UtcDateTime;
                Utilities.Log("Creating the virtual machines");

                List<VirtualMachineResource> allCreatableVirtualMachines = new List<VirtualMachineResource>();
                allCreatableVirtualMachines.AddRange(frontendCreatableVirtualMachines);
                allCreatableVirtualMachines.AddRange(backendCreatableVirtualMachines);


                var endTime = DateTimeOffset.Now.UtcDateTime;
                Utilities.Log("Created virtual machines");

                foreach (var virtualMachine in allCreatableVirtualMachines)
                {
                    if (virtualMachine != null)
                    Utilities.Log(virtualMachine.Id);
                }

                Utilities.Log($"Virtual machines create: took {(endTime - startTime).TotalSeconds } seconds");
            }
            finally
            {
                Utilities.Log($"Deleting resource group : {rgName}");
                await resourceGroup.DeleteAsync(WaitUntil.Completed);
                Utilities.Log($"Deleted resource group : {rgName}");
            }
        }

        public static async Task Main(string[] args)
        {
            try
            {
                //=============================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                // Print selected subscription
                Utilities.Log("Selected subscription: " + client.GetSubscriptions().Id);

                await RunSample(client);
            }
            catch (Exception ex)
            {
                Utilities.Log(ex);
            }
        }
    }
}