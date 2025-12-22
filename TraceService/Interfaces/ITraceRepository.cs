using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TraceService.Models;

namespace TraceService.Interfaces
{
    public interface ITraceRepository
    {
        // Zapis
        void SaveLog(TraceLogModel log);

        // Weryfikacja
        bool IsDmcProcessed(int machineId, string dmc1, string dmc2);
        bool IsScrapDetected(string dmc1, string dmc2);

        // Metody do obsługi "Poprzedniej Maszyny"
        // Zwraca dane do połączenia z inną maszyną (IP, Login, Hasło)
        RemoteMachineConfig GetRemoteMachineConfig(int machineId);

        // Pobiera ostatni status z innej maszyny
        (int? Result, DateTime? Timestamp) GetLatestLogEntry(int machineId, string dmc1, string dmc2, bool checkSecondary);
    }
}

