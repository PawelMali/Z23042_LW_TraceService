using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using TraceService.Controlers;
using TraceService.Enums;
using TraceService.Interfaces;
using TraceService.Models;


namespace TraceService
{
    public class PLCController
    {
        // Główne komponenty
        private readonly ILogger _logger;
        private readonly ITraceRepository _repository;
        private readonly IPlcDriver _driver;
        private readonly MessageQueueService _mqService;

        // Dane konfiguracyjne
        public String ID { get; private set; }
        public String Name { get; private set; }
        public String IP { get; private set; }
        public String Type { get; private set; } // 1 - Siemens or 2 - Allen-Bradley
        public Boolean activation { get; set; }

        // Pola biznesowe
        public Byte NumbersOfParameters { get; private set; }
        public Boolean CheckOnlyOnce { get; private set; }
        public TemplatePreviousMachine PreviousMachine1 { get; private set; }
        public TemplatePreviousMachine PreviousMachine2 { get; private set; }
        public TemplatePreviousMachine PreviousMachine3 { get; private set; }

        // Dane do łączenia z Master DB (potrzebne do sprawdzania innych maszyn)
        private readonly string _dbLocalServer;
        private readonly string _dbLocalPort;
        private readonly string _dbLocalDb;
        private readonly string _dbLocalUser;
        private readonly string _dbLocalPass;

        // Kontrola procesu
        public Task ProcessTask { get; private set; }
        public Boolean IsRunning { get; private set; }
        public Boolean IsConnected => _driver != null && _driver.IsConnected;
        public CancellationTokenSource ProcessTokenSource { get; private set; }
        public Int32 ErrorCount = 0;

        public PLCController(
            ILogger globalLogger, // Można przekazać globalny lub stworzyć wewnątrz
            MessageQueueService mqService, // Wstrzykujemy kolejkę mqtt
            String id, String name, String ip, Byte type,
            String dbread, String dbwrite, String dbresult,
            Byte numbersofparameters, Boolean checkonlyonce,
            TemplatePreviousMachine previousmachine1, TemplatePreviousMachine previousmachine2, TemplatePreviousMachine previousmachine3,
            String dbLocalServer, String dbLocalPort, String dbLocalDb, String dbLocalUser, String dbLocalPass)
        {
            _mqService = mqService;
            ID = id;
            Name = name;
            IP = ip;
            NumbersOfParameters = numbersofparameters;
            CheckOnlyOnce = checkonlyonce;
            PreviousMachine1 = previousmachine1;
            PreviousMachine2 = previousmachine2;
            PreviousMachine3 = previousmachine3;

            // Zapisujemy dane Master DB do użycia przy sprawdzaniu poprzednich maszyn
            _dbLocalServer = dbLocalServer;
            _dbLocalPort = dbLocalPort;
            _dbLocalDb = dbLocalDb;
            _dbLocalUser = dbLocalUser;
            _dbLocalPass = dbLocalPass;

            // 1. Inicjalizacja Loggera (dedykowany plik dla maszyny)
            _logger = new LoggerConfiguration()
                .WriteTo.File(
                    path: $@"C:\Trace\{ID}\{ID}_log_.txt",
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} | {Message:lj}{NewLine}{Exception}",
                    shared: true
                )
                .CreateLogger();

            // 2. Inicjalizacja Repozytorium
            _repository = new SqlTraceRepository(_dbLocalServer, _dbLocalPort, _dbLocalDb, _dbLocalUser, _dbLocalPass);

            // 3. Inicjalizacja Sterownika (Strategia)
            if (type == 1)
            {
                Type = "Siemens";
                _driver = new SiemensPlcDriver(_logger, int.Parse(ID), IP, dbread, dbwrite, dbresult, numbersofparameters == 2);
            }
            else
            {
                Type = "Allen-Bradley";
                _driver = new AllenBradleyPlcDriver(_logger, int.Parse(ID), IP, dbread, dbwrite, dbresult, numbersofparameters == 2);
            }

            ProcessTokenSource = new CancellationTokenSource();

            // Próba pierwszego połączenia (opcjonalna, proces i tak ma retry)
            // _driver.Connect(); 
        }

        public void StartProcess()
        {
            _logger.Information("START PLC PROCESS");
            IsRunning = true;
            ProcessTask = Task.Run(() => ControllerProcess(ProcessTokenSource.Token), ProcessTokenSource.Token);
        }

        public void StopProcess()
        {
            if (!IsRunning) return;

            IsRunning = false;
            _logger.Information("STOP PLC PROCESS");
            ProcessTokenSource.Cancel();
            try
            {
                ProcessTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.Error($"Error stopping task: {ex.Message}");
            }

            _driver.Disconnect();
            _driver.Dispose();

            try { ProcessTask.Dispose(); } 
            catch { }
        }

        public void SendStatus(string status)
        {
            SendNetMqMessage(status);
        }

        // --- GŁÓWNA PĘTLA STEROWANIA ---
        private async Task ControllerProcess(CancellationToken token)
        {
            int lastCheckLife = -1;
            int lastCheckOK = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1. Obsługa połączenia (Self-Healing)
                    if (!_driver.IsConnected)
                    {
                        SendNetMqMessage("Not connected to PLC");
                        _driver.Connect();
                        if (!_driver.IsConnected)
                        {
                            await Task.Delay(5000, token); // Czekaj przed ponowną próbą
                            continue;
                        }
                    }

                    // 2. Heartbeat (Life Bit) - co sekundę
                    if (DateTime.Now.Second != lastCheckLife)
                    {
                        _driver.WriteLifeBit();
                        lastCheckLife = DateTime.Now.Second;
                    }

                    // 3. Sprawdzenie aktywacji
                    if (!activation)
                    {
                        SendNetMqMessage("Not activated");
                        await Task.Delay(2000, token);
                        continue;
                    }

                    // 4. Odczyt stanu sterowania
                    var controlState = _driver.ReadControlState();

                    // 5. Maszyna Stanów
                    switch (controlState.TaskSendToPc)
                    {
                        case 1: // Weryfikacja (Check)
                            if (controlState.TaskConfirmFromPc != 1)
                            {
                                PerformVerification();
                            }
                            break;

                        case 2: // Zapis (Save)
                            if (controlState.TaskConfirmFromPc != 2)
                            {
                                PerformSave();
                            }
                            break;

                        default: // Idle / Reset
                            if (controlState.TaskConfirmFromPc != 0)
                            {
                                _driver.WriteStatus(0, 0, 0); // Reset wszystkiego
                            }
                            break;
                    }

                    await Task.Delay(100, token); // Krótki sleep dla odciążenia CPU

                    lastCheckOK++;
                    if (lastCheckOK > 4) // Co ok. 500ms
                    {
                        if (controlState.TaskSendToPc == 0)
                            SendNetMqMessage("OK"); 
                        if (controlState.TaskSendToPc == 1)
                            SendNetMqMessage("Read Data"); 
                        if (controlState.TaskSendToPc == 2)
                            SendNetMqMessage("Save Data"); 

                        lastCheckOK = 0;
                    }
                }
                catch (Exception ex)
                {
                    ErrorCount++;
                    _logger.Error($"Controller Loop Error: {ex.Message}");
                    SendNetMqMessage("Error");
                    await Task.Delay(2000, token); // Zwolnij w przypadku błędów
                }
            }
        }

        private void PerformVerification()
        {
            _logger.Information("Verification Task Started");
            SendNetMqMessage("Read Data");

            // 1. Pobierz TraceData (tylko po to, żeby mieć kody DMC)
            // Wygodniej byłoby mieć w Driverze metodę GetDmcOnly(), ale ReadTraceData też zadziała
            TraceLogModel data;
            try
            {
                data = _driver.ReadTraceData();
            }
            catch (Exception ex)
            {
                _logger.Error($"Read DMC Error: {ex.Message}");
                // Błąd odczytu -> Błąd komunikacji lub danych
                _driver.WriteStatus(2, (short)TraceErrorStatus.OtherError, 1);
                return;
            }

            string dmc1 = data.DmcCode1;
            string dmc2 = data.DmcCode2;

            // 2. Logika Biznesowa (ta zoptymalizowana wcześniej)
            var result = CheckInDatabaseLogic(dmc1, dmc2);

            // 3. Wysłanie wyniku do PLC
            _driver.WriteStatus(result.PartStatus, (short)result.ErrorStatus, 1);
        }

        private void PerformSave()
        {
            _logger.Information("Save Task Started");
            SendNetMqMessage("Save Data");
            try
            {
                // 1. Pobierz dane
                var data = _driver.ReadTraceData();

                // 2. Walidacja DMC
                if (string.IsNullOrEmpty(data.DmcCode1) && string.IsNullOrEmpty(data.DmcCode2))
                {
                    throw new Exception("No DMC Code");
                }

                // 3. Zapis do repozytorium
                _repository.SaveLog(data);

                // 4. Potwierdzenie sukcesu
                _logger.Information($"Data Saved Successfully. DMC: {data.DmcCode1}, {data.DmcCode2}");
                _driver.WriteStatus(0, 0, 2); // PartStatus/ErrorStatus nieistotne przy Save? W oryginale Error=0
            }
            catch (Exception ex)
            {
                _logger.Error($"Save Task Error: {ex.Message}");
                short errCode = (short)(ex.Message == "No DMC Code" ? TraceErrorStatus.NoDmcCode : TraceErrorStatus.DatabaseSaveError);
                _driver.WriteStatus(0, errCode, 2);
            }
        }

        // --- Logika Biznesowa (Przeniesiona i dostosowana) ---
        private (short PartStatus, TraceErrorStatus ErrorStatus) CheckInDatabaseLogic(string dmc1, string dmc2)
        {
            TraceDiagnostics diag = new TraceDiagnostics(_logger, ID, dmc1, dmc2);
            try
            {
                // 0. czy kod dmc jest pusty?
                if (string.IsNullOrEmpty(dmc1) && string.IsNullOrEmpty(dmc2))
                    return diag.SetResult(2, TraceErrorStatus.NoDmcCode);


                bool isScrap = false;
                bool isProcessed = false;

                try
                {
                    // 1. Scrap Lokalny
                    isScrap = diag.Measure("IsScrapDetected", () => _repository.IsScrapDetected(dmc1, dmc2));
                    if (isScrap) return diag.SetResult(2, TraceErrorStatus.ScrapDetected);

                    // 2. CheckOnlyOnce - sprawdzenie czy już był
                    if (CheckOnlyOnce)
                    {
                        isProcessed = diag.Measure("IsDmcProcessed", () => _repository.IsDmcProcessed(int.Parse(ID), dmc1, dmc2));
                        if (isProcessed) return diag.SetResult(2, TraceErrorStatus.AlreadyProcessed);
                    }
                }
                catch (SqlException)
                {
                    // Błąd lokalnej bazy -> Błąd krytyczny
                    return diag.SetResult(2, TraceErrorStatus.DatabaseConnectionError);
                }

                // 3. Poprzednie maszyny
                var machines = new List<TemplatePreviousMachine>();
                if (PreviousMachine1.MachineID > 0) machines.Add(PreviousMachine1);
                if (PreviousMachine2.MachineID > 0) machines.Add(PreviousMachine2);
                if (PreviousMachine3.MachineID > 0) machines.Add(PreviousMachine3);

                // 3.1 Pierwsza stacja linii. Brak poprzednich maszyn do sprawdzenia.
                if (machines.Count == 0)
                    return diag.SetResult(1, TraceErrorStatus.Ok);

                var validResults = new List<MachineCheckResult>();
                bool anyOldData = false;

                foreach (var m in machines)
                {
                    var check = diag.Measure($"Check_{m.MachineID}", () => GetMachineData(m, dmc1, dmc2));
                    // SPRAWDZENIE BŁĘDU POŁĄCZENIA
                    if (check.ErrorStatus == TraceErrorStatus.DatabaseConnectionError)
                    {
                        // Jeśli nie możemy połączyć się z poprzednią maszyną, nie możemy zweryfikować procesu.
                        // Fail-Safe: Status 2.
                        return diag.SetResult(2, TraceErrorStatus.DatabaseConnectionError);
                    }

                    if (check.IsFound)
                    {
                        // REGUŁA 1: Scrap z jakiegokolwiek stanowiska = Scrap
                        if (check.IsScrap) return diag.SetResult(2, TraceErrorStatus.ScrapDetected);
                        if (check.IsOld) anyOldData = true;
                        else validResults.Add(check);
                    }
                }

                if (validResults.Count == 0)
                {
                    // Jeśli nie mamy żadnych ważnych wyników, sprawdź czy były stare dane
                    return diag.SetResult(2, anyOldData
                    ? TraceErrorStatus.PreviousMachineOldData
                    : TraceErrorStatus.PreviousMachineNotFound);
                }

                // Sortuj po dacie malejąco
                validResults.Sort((a, b) => b.Date.CompareTo(a.Date));
                var freshest = validResults[0];

                // REGUŁA 2: Bierzemy dane z najświeższego stanowiska
                if (freshest.Result == 1 || freshest.Result == 3)
                    return diag.SetResult(1, TraceErrorStatus.Ok);
                if (freshest.Result == 2)
                    return diag.SetResult(2, TraceErrorStatus.NokDetected);
                if (freshest.Result == 0) 
                    return diag.SetResult(2, TraceErrorStatus.StatusMissing);

                return diag.SetResult(2, TraceErrorStatus.OtherError);
            }
            catch (Exception ex)
            {
                // W razie wyjątku rzucamy go dalej (zostanie złapany w ControllerProcess),
                // ale TraceDiagnostics.Dispose() i tak się wywoła i zaloguje czasy oraz brak wyniku.
                if (ex is SqlException)
                    return diag.SetResult(2, TraceErrorStatus.DatabaseConnectionError);

                return diag.SetResult(2, TraceErrorStatus.OtherError);
            }
            finally
            {
                diag.Dispose();
            }
        }

        private MachineCheckResult GetMachineData(TemplatePreviousMachine machine, string dmc1, string dmc2)
        {
            var output = new MachineCheckResult { IsFound = false, IsOld = false };

            try
            {
                // 1. Konfiguracja i połączenie

                string targetIp = (machine != null && !string.IsNullOrEmpty(machine.DbIp)) ? machine.DbIp : _dbLocalServer;
                string targetUser = (machine != null && !string.IsNullOrEmpty(machine.DbUser)) ? machine.DbUser : _dbLocalUser;
                string targetPass = (machine != null && !string.IsNullOrEmpty(machine.DbPassword)) ? machine.DbPassword : _dbLocalPass;


                ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
                SqlTraceRepository remoteRepo;
                if (machine.MachineID == 50)
                    remoteRepo = new SqlTraceRepository(targetIp, _dbLocalPort, "TRACE_BACKUP_EK0", targetUser, targetPass);
                else
                    remoteRepo = new SqlTraceRepository(targetIp, _dbLocalPort, _dbLocalDb, targetUser, targetPass);
                //var remoteRepo = new SqlTraceRepository(targetIp, _dbLocalPort, _dbLocalDb, targetUser, targetPass);


                var logEntry = remoteRepo.GetLatestLogEntry(machine.MachineID, dmc1, dmc2, machine.CheckSecondaryCode);

                if (logEntry.Result == null || logEntry.Timestamp == null) return output;

                output.IsFound = true;
                output.Result = logEntry.Result.Value;
                output.Date = logEntry.Timestamp.Value;

                if (machine.MaxDaysNumber > 0)
                {
                    if ((DateTime.Now - output.Date).TotalDays > machine.MaxDaysNumber) output.IsOld = true;
                }
                return output;
            }
            catch (SqlException ex) // Łapiemy błędy SQL (timeout, login failed, network unreachable)
            {
                _logger.Error($"GetMachineData SQL Error ID {machine.MachineID}: {ex.Message}");
                output.ErrorStatus = TraceErrorStatus.DatabaseConnectionError; // Oznaczamy błąd!
                return output;
            }
            catch (Exception ex)
            {
                _logger.Error($"GetMachineData Generic Error ID {machine.MachineID}: {ex.Message}");
                output.ErrorStatus = TraceErrorStatus.OtherError;
                return output;
            }
        }

        // --- METODA POMOCNICZA NETMQ (Zastępuje starą NetMQ_SendPLCList) ---
        private void SendNetMqMessage(string status)
        {
            try
            {
                object readStruct = null;
                object writeStruct = null;
                object resultObject = null; // To będzie TemplateResult...

                if (_driver != null && _driver.IsConnected)
                {
                    // Pobieramy struktury sterujące
                    var ctrl = _driver.GetControlStructures();
                    readStruct = ctrl.Read;
                    writeStruct = ctrl.Write;

                    // Pobieramy obiekt wizualizacji (już zmapowany na real_1, int_1 itp.)
                    resultObject = _driver.GetVisualisationObject();
                }

                var msg = new TemplateSendPLCList
                {
                    MachineID = this.ID,
                    MachineName = this.Name,
                    Type = this.Type,
                    IP = this.IP,
                    IsRunning = this.IsRunning,
                    Status = status,
                    MessageTime = DateTime.Now,

                    readData = readStruct,
                    writeData = writeStruct,
                    resultData = resultObject 
                };

                string json = JsonConvert.SerializeObject(msg, Formatting.None);
                _mqService.EnqueueMessage(json);
            }
            catch (Exception ex)
            {
                // _logger.Warning($"MQ Error: {ex.Message}");
            }
        }









    }
}
