using System.Collections.Generic;
using System.Net;
using System.ServiceProcess;
using System.Threading.Tasks;
using ARSoft.Tools.Net.Dns;

namespace DnsProxy
{
    public partial class Service : ServiceBase
    {
        private DnsServer[] _servers;

        public Service()
        {
            InitializeComponent();
            InitializeDnsServers();
        }

        private void InitializeDnsServers()
        {
            var servers = new List<DnsServer>();

            var listenerIps = ListenerLocator.GetNatIpAddresses();
            foreach (var listenerIp in listenerIps)
            {
                IPAddress ip;

                if (IPAddress.TryParse(listenerIp, out ip))
                {
                    var dnsServer = new DnsServer(ip, 10, 10);
                    dnsServer.QueryReceived += OnQueryReceived;
                    servers.Add(dnsServer);
                }
            }
            _servers = servers.ToArray();
        }

#if DEBUG
        public void DebugRun(string[] args)
        {
            OnStart(args);
        }
#endif

        protected override void OnStart(string[] args)
        {
            StartAllServers();
        }

        private void StartAllServers()
        {
            foreach (var dnsServer in _servers)
            {
                dnsServer.Start();
            }
        }

        protected override void OnPause()
        {
            StopAllServers();
        }

        private void StopAllServers()
        {
            foreach (var dnsServer in _servers)
            {
                dnsServer.Stop();
            }
        }

        protected override void OnContinue()
        {
            StartAllServers();
        }

        protected override void OnStop()
        {
            StopAllServers();
        }

        private static async Task OnQueryReceived(object sender, QueryReceivedEventArgs e)
        {
            var message = e.Query as DnsMessage;

            var response = message?.CreateResponseInstance();

            if (message?.Questions.Count == 1)
            {
                // send query to upstream _servers
                var question = message.Questions[0];

                var upstreamResponse = await DnsClient.Default.ResolveAsync(question.Name, question.RecordType, question.RecordClass);

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
    }
}
