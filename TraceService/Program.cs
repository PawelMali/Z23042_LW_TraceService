using DotNetEnv;
using System;
using System.IO;
using System.ServiceProcess;

namespace TraceService
{
    internal static class Program
    {
        static void Main()
        {
            Env.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"));

            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new MainService()
            };
            ServiceBase.Run(ServicesToRun);
        }
    }
}
