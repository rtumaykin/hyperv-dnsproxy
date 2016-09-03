using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ARSoft.Tools.Net.Dns;

namespace DnsProxy
{
    public static class ServiceManager
    {
        #region Private variables

        private static readonly Dictionary<string, DnsServer> Servers = new Dictionary<string, DnsServer>();

        private static Timer _scheduler;
        private static readonly object Lock = new object();

        #endregion // Private variables

        #region WMI data retrievers

        private static IEnumerable<string> GetNetworkInterfaceIpAddresses(uint[] virtualAdaptersInterfaceIds)
        {
            if (virtualAdaptersInterfaceIds == null || virtualAdaptersInterfaceIds.Length == 0)
                yield break;

            var scope = new ManagementScope(@"root\StandardCimv2");
            var queryString = "SELECT * FROM MSFT_NetIPAddress WHERE " +
                              string.Join(" OR ", virtualAdaptersInterfaceIds.Select(i => $"InterfaceIndex = {i}"));

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


        private static IEnumerable<uint> GetVirtualAdaptersInterfaceIds(string [] internalEthernetDeviceIds)
        {
            if (internalEthernetDeviceIds == null || internalEthernetDeviceIds.Length == 0)
                yield break;

            var scope = new ManagementScope(@"root\StandardCimv2");
            var queryString = "SELECT * FROM MSFT_NetAdapter WHERE " +
                              string.Join(" OR ", internalEthernetDeviceIds.Select(d => $"DeviceID = \"{{{d}}}\""));

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

        private static IEnumerable<string> GetInternalEthernetDeviceIds()
        {
            var scope = new ManagementScope(@"root\virtualization\v2");
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

        #endregion // WMI data retrievers


        #region Service Management Methods
        public static void Start()
        {

            _scheduler = new Timer(Cycle, null, 0, 15000);
        }


        private static void Cycle(object o)
        {

            lock (Lock)
            {
                var natSubnets = GetNatSubnets().ToArray();
                var internalEthernetDeviceIds = GetInternalEthernetDeviceIds().ToArray();
                var virtualAdaptersInterfaceIds = GetVirtualAdaptersInterfaceIds(internalEthernetDeviceIds).ToArray();
                var networkInterfaceIpAddresses = GetNetworkInterfaceIpAddresses(virtualAdaptersInterfaceIds).ToArray();
                var ipAddressesForBinding = GetIpAddressesForBinding(networkInterfaceIpAddresses, natSubnets).ToArray();

                var currentlyBoundAddresses = Servers.Keys.ToArray();

                foreach (
                    var ipAddressForBinding in
                        ipAddressesForBinding.Where(a => !currentlyBoundAddresses.Contains(a)))
                {
                    BindListener(ipAddressForBinding);
                }

                foreach (
                    var ipAddressForBinding in
                        currentlyBoundAddresses.Where(a => !ipAddressesForBinding.Contains(a)))
                {
                    UnBindListener(ipAddressForBinding);
                }
            }

        }


        private static void BindListener(string ipAddressForBinding)
        {
            IPAddress ip;

            if (!IPAddress.TryParse(ipAddressForBinding, out ip))
                return;

            if (Servers.ContainsKey(ipAddressForBinding))
                return;

            var dnsServer = new DnsServer(ip, 10, 10);
            dnsServer.QueryReceived += OnQueryReceived;
            Servers.Add(ipAddressForBinding, dnsServer);
            dnsServer.Start();

            var appLog = new System.Diagnostics.EventLog {Source = "Hyper-V Dns Proxy"};
            appLog.WriteEntry($"Started DNS Service on {ipAddressForBinding}");
        }

        private static async Task OnQueryReceived(object sender, QueryReceivedEventArgs e)
        {
            var message = e.Query as DnsMessage;

            var response = message?.CreateResponseInstance();

            if (message?.Questions.Count == 1)
            {
                // send query to upstream _servers
                var question = message.Questions[0];

                var upstreamResponse =
                    await DnsClient.Default.ResolveAsync(question.Name, question.RecordType, question.RecordClass);

                // if got an answer, copy it to the message sent to the client
                if (upstreamResponse != null)
                {
                    foreach (var record in (upstreamResponse.AnswerRecords))
                    {
                        response.AnswerRecords.Add(record);
                    }
                    foreach (var record in (upstreamResponse.AdditionalRecords))
                    {
                        response.AdditionalRecords.Add(record);
                    }

                    response.ReturnCode = ReturnCode.NoError;

                    // set the response
                    e.Response = response;
                }
            }
        }

        private static void UnBindListener(string ipAddressForBinding)
        {
            DnsServer server;
            if (!Servers.TryGetValue(ipAddressForBinding, out server))
                return;

            server.Stop();
            Servers.Remove(ipAddressForBinding);
            var appLog = new System.Diagnostics.EventLog { Source = "Hyper-V Dns Proxy" };
            appLog.WriteEntry($"Stopped DNS Service on {ipAddressForBinding}");
        }


        private static IEnumerable<string> GetIpAddressesForBinding(string[] networkInterfaceIpAddresses, string[] natSubnets)
        {
            if (networkInterfaceIpAddresses == null || natSubnets == null)
                yield break;

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

        public static void Stop()
        {
            _scheduler.Dispose();
            _scheduler = null;
            UnbindListeners();
        }

        private static void UnbindListeners()
        {
            lock (Lock)
            {
                var ipAddressesForUnBinding = Servers.Keys.ToArray();

                foreach (var ipAddressForUnbinding in ipAddressesForUnBinding)
                {
                    UnBindListener(ipAddressForUnbinding);
                }
            }
        }

        #endregion // Service Management Methods
    }
}
