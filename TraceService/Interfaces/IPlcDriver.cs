using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TraceService.Models;

namespace TraceService.Interfaces
{
    public interface IPlcDriver : IDisposable
    {
        bool IsConnected { get; }

        // Inicjalizacja połączenia
        void Connect();
        void Disconnect();

        // Obsługa sygnału życia (Watchdog)
        void WriteLifeBit();

        // Odczyt komend sterujących (np. "Czytaj DMC", "Zapisz wynik")
        PlcControlState ReadControlState();

        // Wysłanie odpowiedzi do PLC (status części, błąd, potwierdzenie zadania)
        void WriteStatus(short partStatus, short errorStatus, short taskConfirm);

        // Odczyt pełnych danych procesowych (zmapowanych już na nasz model bazodanowy)
        TraceLogModel ReadTraceData();

        // Zwraca obiekt gotowy do wysłania przez NetMQ (TemplateResultLongDataList lub Short)
        object GetVisualisationObject();

        // Zwraca struktury sterujące (Read/Write)
        (object Read, object Write) GetControlStructures();
    }
}
