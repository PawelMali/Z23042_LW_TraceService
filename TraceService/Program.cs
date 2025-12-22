using DotNetEnv;
using System;
using System.IO;
using System.ServiceProcess;

namespace TraceService
{
    internal static class Program
    {
        // Zmień sygnaturę Main, aby przyjmowała argumenty (opcjonalne, ale dobra praktyka)
        static void Main(string[] args)
        {
            // Ładowanie zmiennych środowiskowych (wspólne dla obu trybów)
            Env.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"));

            // Utwórz instancję usługi
            MainService service = new MainService();

            if (Environment.UserInteractive)
            {
                // ==========================================
                // TRYB DEBUGOWANIA (Uruchomienie w Visual Studio)
                // ==========================================
                service.RunAsConsole(args);
            }
            else
            {
                // ==========================================
                // TRYB USŁUGI (Normalne działanie w tle)
                // ==========================================
                ServiceBase[] ServicesToRun;
                ServicesToRun = new ServiceBase[]
                {
                    service
                };
                ServiceBase.Run(ServicesToRun);
            }
        }
    }
}
