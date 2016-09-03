using System.Collections.Generic;
using System.Net;
using System.ServiceProcess;
using System.Threading.Tasks;
using ARSoft.Tools.Net.Dns;

namespace DnsProxy
{
    public partial class Service : ServiceBase
    {
        public Service()
        {
            InitializeComponent();
        }

#if DEBUG
        public void DebugRun(string[] args)
        {
            OnStart(args);
        }
#endif

        protected override void OnStart(string[] args)
        {
            ServiceManager.Start();
        }

        protected override void OnPause()
        {
            ServiceManager.Stop();
        }

        protected override void OnContinue()
        {
            ServiceManager.Start();
        }

        protected override void OnStop()
        {
            ServiceManager.Stop();
        }

        protected override void OnShutdown()
        {
            ServiceManager.Stop();
            base.OnShutdown();
        }
    }
}
