using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TraceService.Enums
{
    public enum TraceErrorStatus : short
    {
        Ok = 0,
        NoDmcCode = 1,              // Brak kodu DMC
        NoDMCDefined = 2,           // Nie zdefiniowano DMC w bazie linii
        DatabaseConnectionError = 3,// Błąd połączenia SQL
        AlreadyProcessed = 4,       // Już przetworzono (dla CheckOnlyOnce)

        ScrapDetected = 50,         // Znaleziono status 4 SCRAP (NOK krytyczny)
        NokDetected = 51,           // Znaleziono status 2 NOK
        PreviousMachineNotFound = 52, // Nie znaleziono w ogóle w bazie
        PreviousMachineOldData = 53,  // Dane zbyt stare (limit dni)
        StatusMissing = 55,         // Dane zbyt stare (limit dni)

        DatabaseSaveError = 70,     // Błąd zapisu do bazy
        DatabaseCheckError = 71,     // Błąd sprawdzenia w bazy

        OtherError = 99             // Inne błędy
    }
}
