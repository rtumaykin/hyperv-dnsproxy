using System.Collections.Generic;
using System.Management;
using System.Net;

namespace DnsProxy
{
    public static class ListenerLocator
    {
        public static string[] GetNatIpAddresses()
        {
            var natIpAdresses = new List<string>();
            var scope = new ManagementScope(@"root\virtualization\v2");
            var query = new ObjectQuery("select * from Msvm_VirtualEthernetSwitchSettingData");
            using (var queryExecute = new ManagementObjectSearcher(scope, query))
            {
                using (var allSwitches = queryExecute.Get())
                {
                    foreach (var sw in allSwitches)
                    {
                        var ipAdress = GetNatSubnetAddress(sw.Properties);
                        if (!string.IsNullOrWhiteSpace(ipAdress))
                        {
                            var ipAddressBytes = IPAddress.Parse(ipAdress).GetAddressBytes();

                            natIpAdresses.Add($"{ipAddressBytes[0]}.{ipAddressBytes[1]}.{ipAddressBytes[2]}.{ipAddressBytes[3] + 1}");    
                        }
                    }
                    return natIpAdresses.ToArray();
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
