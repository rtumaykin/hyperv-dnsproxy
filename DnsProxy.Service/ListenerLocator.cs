using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;

namespace DnsProxy
{
    public static class ListenerLocator
    {
        public static IEnumerable<string> GetIpAddressesForBinding()
        {
            // Get active nat subnets
            var natSubnets = GetNatSubnets().ToArray();
            var internalEthernetDevices = GetInternalEthernetDevices();
            var networkInterfaceIndexes = GetVirtualAdaptersInterfaceIds(internalEthernetDevices);
            var networkInterfaceIpAddresses = GetNetworkInterfaceIpAddresses(networkInterfaceIndexes);
            foreach (var networkInterfaceIpAddress in networkInterfaceIpAddresses)
            {
                var ipParts = networkInterfaceIpAddress.Split('/');
                var interfaceIp = BitConverter.ToInt32(IPAddress.Parse(ipParts[0]).GetAddressBytes(), 0);
                var interfaceMask = IPAddress.HostToNetworkOrder(-1 << (32 - int.Parse(ipParts[1])));
                foreach (var natSubnet in natSubnets)
                {
                    var subnetParts = natSubnet.Split('/');
                    var subnetIp = BitConverter.ToInt32(IPAddress.Parse(subnetParts[0]).GetAddressBytes(), 0);
                    var subnetMask = IPAddress.HostToNetworkOrder(-1 << (32 - int.Parse(subnetParts[1])));

                    if ((interfaceIp & interfaceMask) == (subnetIp & subnetMask))
                    {
                        yield return ipParts[0];
                    }

                }
            }
        }

        private static IEnumerable<string> GetNetworkInterfaceIpAddresses(IEnumerable<uint> networkInterfaceIndexes)
        {

            var scope = new ManagementScope(@"root\StandardCimv2");
            var queryString = "SELECT * FROM MSFT_NetIPAddress WHERE " +
                              string.Join(" OR ", networkInterfaceIndexes.Select(i => $"InterfaceIndex = {i}"));

            var query = new ObjectQuery(queryString);
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                using (var ipAddresses = searcher.Get())
                {
                    foreach (var ipAddress in ipAddresses)
                    {
                        yield return $"{ipAddress["IPAddress"]}/{ipAddress["PrefixLength"]}";
                    }
                }
            }
        }

        private static IEnumerable<string> GetInternalEthernetDevices()
        {
            var internalEthernetDeviceIds = new List<string>();

            var scope = new ManagementScope(@"root\virtualization\v2");
            // Get-WmiObject -Query "select * from Msvm_VirtualEthernetSwitch" -Namespace "root\virtualization\v2"
            var query = new ObjectQuery("select * from Msvm_InternalEthernetPort");
            using (var results = new ManagementObjectSearcher(scope, query))
            {
                using (var internalEthernetPorts = results.Get())
                {
                    foreach (var internalEthernetPort in internalEthernetPorts)
                    {
                        var fullDeviceId = (string) internalEthernetPort["DeviceID"];
                        var foundMatches = Regex.Matches(fullDeviceId,
                            @"[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}");
                        if (foundMatches.Count > 0)
                        yield return foundMatches[0].Value;
                    }
                }
            }
        }
        private static IEnumerable<string> GetNatSubnets()
        {
            var scope = new ManagementScope(@"root\StandardCimv2");
            var query = new ObjectQuery("SELECT * FROM MSFT_NetNat");
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                using (var natObjects = searcher.Get())
                {
                    foreach (var natObject in natObjects)
                    {
                        if ((byte) natObject["Active"] == 1)
                        {
                            var subnet = (string) natObject["InternalIPInterfaceAddressPrefix"];
                            if (!string.IsNullOrWhiteSpace(subnet))
                            {
                                yield return subnet;
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<uint> GetVirtualAdaptersInterfaceIds(IEnumerable<string> adapterDeviceIds)
        {
            var scope = new ManagementScope(@"root\StandardCimv2");
            var queryString = "SELECT * FROM MSFT_NetAdapter WHERE " +
                              string.Join(" OR ", adapterDeviceIds.Select(d => $"DeviceID = \"{{{d}}}\""));

            var query = new ObjectQuery(queryString);
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                using (var virtualAdapters = searcher.Get())
                {
                    foreach (var virtualAdapter in virtualAdapters)
                    {
                        yield return (uint) virtualAdapter["InterfaceIndex"];
                    }
                }
            }
        }

        private static string GetNatSubnetAddress(PropertyDataCollection properties)
        {
            var natEnabled = false;
            var natSubnetAddress = "";

            foreach (var property in properties)
            {
                switch (property.Name)
                {
                    case "NATEnabled":
                        natEnabled = (bool) property.Value;
                        break;
                    case "NATSubnetAddress":
                        natSubnetAddress = property.Value?.ToString();
                        break;
                }
            }
            return natEnabled ? natSubnetAddress : null;
        }
    }
}
