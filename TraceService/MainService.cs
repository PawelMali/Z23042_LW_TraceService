using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition.Primitives;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using TraceService.Controlers;

namespace TraceService
{
    public partial class MainService : ServiceBase
    {
        private List<PLCController> controllers = new List<PLCController>();

        // Konfiguracja Master DB (Env)
        private readonly String DBLocalServer = Environment.GetEnvironmentVariable("DATABASE_SERVER");
        private readonly String DBLocalPort = Environment.GetEnvironmentVariable("DATABASE_PORT");
        private readonly String DBLocalDatabase = Environment.GetEnvironmentVariable("DATABASE_DATABASE");
        private readonly String DBLocalUser = Environment.GetEnvironmentVariable("DATABASE_USER");
        private readonly String DBLocalPassword = Environment.GetEnvironmentVariable("DATABASE_PASSWORD");
        // Debug
        // private readonly Boolean Debug = Environment.GetEnvironmentVariable("DEBUG").ToUpper() == "TRUE"; // Nieużywane w nowej architekturze?

        private readonly String secretStart = "Jz1wGOi8Ql33mHxEMwrmfZjqh95BDcrq";
        private readonly String secretEnd = "wLAD2KjXHDpaTLUI5MbvyafzaojE4107";

        private readonly MessageQueueService messageQueueService = new MessageQueueService();
        private readonly ILogger _logger;

        private CancellationTokenSource _cancellationTokenSource;

        public MainService()
        {
            InitializeComponent();

            // Globalny logger serwisu
            _logger = new LoggerConfiguration()
                .WriteTo.File(
                    path: @"C:\Trace\Service\service_event_.txt",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 60,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} | {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();
        }

        protected override void OnStart(String[] args)
        {
            _logger.Information("Service Starting... Version: v2.01 - 2026.01.22 (Refactored)");

            messageQueueService.NetMQ_Start();
            //Task.Run(() => messageQueueService.StartProcessingQueue());

            // 1. Tworzymy nowe źródło tokena (resetujemy pilota)
            _cancellationTokenSource = new CancellationTokenSource();

            // 2. Pobieramy sam token
            var token = _cancellationTokenSource.Token;

            // Uruchomienie inicjalizacji w tle
            // Używamy Task.Run, aby OnStart mógł się zakończyć i oddać sterowanie systemowi,
            // podczas gdy InitializeServiceAsync będzie pracować w tle.
            Task.Run(() => InitializeServiceAsync(token), token);
        }

        protected override void OnStop()
        {
            try
            {
                _logger.Information("Service stopping...");

                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                }

                // 1. Zatrzymanie NetMQ
                if (messageQueueService != null)
                {
                    messageQueueService.NetMQ_Stop();
                }

                // 2. Zatrzymujemy Kontrolery RÓWNOLEGLE
                // Wcześniej pętla foreach zatrzymywała je jeden po drugim. 
                // Jeśli każdy czeka 5 sekund na wątek, przy 10 maszynach daje to 50 sekund -> Windows zabije usługę (Timeout).
                Parallel.ForEach(controllers, controller =>
                {
                    try
                    {
                        controller.StopProcess();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error stopping controller {controller.ID}: {ex.Message}");
                    }
                });

                _logger.Information("Service stopped successfully.");

                
            }
            catch (Exception ex)
            {
                // CRITICAL: Zapisz do Event Logu, bo pliki mogą nie działać
                using (System.Diagnostics.EventLog eventLog = new System.Diagnostics.EventLog("Application"))
                {
                    eventLog.Source = "Application";
                    eventLog.WriteEntry($"TraceService OnStop Error: {ex}", System.Diagnostics.EventLogEntryType.Error);
                }
            }
            finally
            {
                // 3. Na samym końcu zamykamy logger
                Log.CloseAndFlush();
                Task.Run(async () =>
                {
                    await Task.Delay(1000); // Czekamy, aż SCM (Windows) ogarnie status
                    Environment.Exit(0);    // Brutalne sprzątanie bibliotek ASComm/NetMQ
                });
            }

        }

        public void RunAsConsole(string[] args)
        {
            Console.WriteLine("--- Uruchamianie usługi w trybie konsoli ---");
            OnStart(args); // Ręczne wywołanie startu

            Console.WriteLine("Usługa działa. Naciśnij dowolny klawisz, aby zatrzymać...");
            Console.ReadKey(); // Czeka na interakcję użytkownika

            OnStop(); // Ręczne wywołanie stopu
            Console.WriteLine("--- Usługa zatrzymana ---");
        }

        private async Task InitializeServiceAsync(CancellationToken token)
        {
            _logger.Information("Checking database connection...");
            Boolean isConnected = await CheckDatabaseConnectionAsync(token);

            if (!isConnected)
            {
                _logger.Error("Database connection failed after multiple retries. Service will not start properly.");
                return;
            }

            try
            {
                var masterRepo = new SqlTraceRepository(DBLocalServer, DBLocalPort, DBLocalDatabase, DBLocalUser, DBLocalPassword);

                string connectionString = $"Data source={DBLocalServer},{DBLocalPort};Initial Catalog={DBLocalDatabase};User ID={DBLocalUser};Password={DBLocalPassword};";

                using (SqlConnection SQLConnection = new SqlConnection(connectionString))
                {
                    await SQLConnection.OpenAsync();

                    // 1. Pobranie Konfiguracji Maszyn
                    using (SqlCommand SQLCommand = SQLConnection.CreateCommand())
                    {
                        SQLCommand.CommandText = @"
                            SELECT 
                                machines.machine_id, machines.machine_name, machines.plc_ip, machines.plc_type, 
                                machines.read_db, machines.write_db, machines.result_db, 
                                machines.numbers_of_parameters, machines.check_only_once, 
                                machines.id_previous_machine_1, machines.max_days_number_1, machines.check_secondary_code_1, 
                                machines.id_previous_machine_2, machines.max_days_number_2, machines.check_secondary_code_2, 
                                machines.id_previous_machine_3, machines.max_days_number_3, machines.check_secondary_code_3, 
                                lines.database_ip, lines.database_login, lines.database_password,
                                machines.passivation_active, machines.passivation_max_days
                            FROM dbo.machines
                            LEFT JOIN dbo.lines ON lines.id = machines.id_line
                            WHERE machines.plc_ip IS NOT NULL AND LEN(machines.plc_ip) > 0 
                            ORDER BY machines.plc_ip, machines.plc_type";

                        using (SqlDataReader SQLreader = await SQLCommand.ExecuteReaderAsync())
                        {
                            String configString = "";

                            while (await SQLreader.ReadAsync())
                            {
                                String plcIP = SQLreader.IsDBNull(2) ? null : SQLreader.GetString(2).Trim();

                                // --- POPRAWKA: Walidacja IP przed utworzeniem obiektu ---
                                if (string.IsNullOrWhiteSpace(plcIP))
                                {
                                    _logger.Warning($"Skipping machine ID {SQLreader[0]} due to empty IP address.");
                                    continue; // Pomijamy tę maszynę i idziemy do następnej
                                }
                                // Opcjonalnie: Prosta walidacja formatu (czy ma kropki)
                                if (plcIP.Split('.').Length < 4)
                                {
                                    _logger.Warning($"Skipping machine ID {SQLreader[0]}: Invalid IP format '{plcIP}'.");
                                    continue;
                                }

                                Byte plcType = SQLreader.IsDBNull(3) ? (Byte)1 : SQLreader.GetByte(3);
                                configString += $"{plcIP}:{plcType};";

                                // Budowanie obiektów poprzednich maszyn
                                //var pm1 = new TemplatePreviousMachine { MachineID = SQLreader.IsDBNull(9) ? 0 : SQLreader.GetInt32(9), MaxDaysNumber = SQLreader.IsDBNull(10) ? (Int16)0 : SQLreader.GetInt16(10), CheckSecondaryCode = SQLreader.IsDBNull(11) ? false : SQLreader.GetBoolean(11) };
                                //var pm2 = new TemplatePreviousMachine { MachineID = SQLreader.IsDBNull(12) ? 0 : SQLreader.GetInt32(12), MaxDaysNumber = SQLreader.IsDBNull(13) ? (Int16)0 : SQLreader.GetInt16(13), CheckSecondaryCode = SQLreader.IsDBNull(14) ? false : SQLreader.GetBoolean(14) };
                                //var pm3 = new TemplatePreviousMachine { MachineID = SQLreader.IsDBNull(15) ? 0 : SQLreader.GetInt32(15), MaxDaysNumber = SQLreader.IsDBNull(16) ? (Int16)0 : SQLreader.GetInt16(16), CheckSecondaryCode = SQLreader.IsDBNull(17) ? false : SQLreader.GetBoolean(17) };

                                // Budowanie obiektów poprzednich maszyn + CACHE KONFIGURACJI
                                var pm1 = CreatePreviousMachine(masterRepo,
                                    SQLreader.IsDBNull(9) ? 0 : SQLreader.GetInt32(9),
                                    SQLreader.IsDBNull(10) ? (Int16)0 : SQLreader.GetInt16(10),
                                    SQLreader.IsDBNull(11) ? false : SQLreader.GetBoolean(11));

                                var pm2 = CreatePreviousMachine(masterRepo,
                                    SQLreader.IsDBNull(12) ? 0 : SQLreader.GetInt32(12),
                                    SQLreader.IsDBNull(13) ? (Int16)0 : SQLreader.GetInt16(13),
                                    SQLreader.IsDBNull(14) ? false : SQLreader.GetBoolean(14));

                                var pm3 = CreatePreviousMachine(masterRepo,
                                    SQLreader.IsDBNull(15) ? 0 : SQLreader.GetInt32(15),
                                    SQLreader.IsDBNull(16) ? (Int16)0 : SQLreader.GetInt16(16),
                                    SQLreader.IsDBNull(17) ? false : SQLreader.GetBoolean(17));

                                // Pobranie danych do logowania lokalnego dla danej maszyny
                                string localDbIp = SQLreader.IsDBNull(18) ? DBLocalServer : SQLreader.GetString(18).Trim();
                                string localDbUser = SQLreader.IsDBNull(19) ? DBLocalUser : SQLreader.GetString(19).Trim();
                                string localDbPass = SQLreader.IsDBNull(20) ? DBLocalPassword : SQLreader.GetString(20).Trim();

                                //Tworzenie Kontrolera(Nowy Konstruktor)
                                var controller = new PLCController(
                                    _logger, // Globalny logger (opcjonalnie używany w środku)
                                    messageQueueService,
                                    SQLreader.IsDBNull(0) ? "0" : SQLreader.GetString(0).Trim(), // ID
                                    SQLreader.IsDBNull(1) ? "Unknown" : SQLreader.GetString(1).Trim(), // Name
                                    plcIP,
                                    plcType,
                                    SQLreader.IsDBNull(4) ? "" : SQLreader.GetString(4).Trim(), // ReadDB / Tag
                                    SQLreader.IsDBNull(5) ? "" : SQLreader.GetString(5).Trim(), // WriteDB / Tag
                                    SQLreader.IsDBNull(6) ? "" : SQLreader.GetString(6).Trim(), // ResultDB / Tag
                                    SQLreader.IsDBNull(7) ? (Byte)1 : SQLreader.GetByte(7), // NumParams
                                    SQLreader.IsDBNull(8) ? false : SQLreader.GetBoolean(8), // CheckOnce
                                    SQLreader.IsDBNull(21) ? false : SQLreader.GetBoolean(21), // CheckMaxProcessings (sql -passivation_active)
                                    SQLreader.IsDBNull(22) ? (Int16)0 : SQLreader.GetInt16(22), // CheckMaxProcessings (sql -passivation_max_days)
                                    pm1, pm2, pm3,
                                    // Credentials do bazy lokalnej (gdzie zapisujemy logi)
                                    DBLocalServer, DBLocalPort, DBLocalDatabase, DBLocalUser, DBLocalPassword
                                );

                                controllers.Add(controller);
                                controller.SendStatus("Loaded controller");
                            }

                            // Weryfikacja aktywacji (CRC32)
                            // Uwaga: Tutaj musimy otworzyć nowe połączenie lub zamknąć reader, bo reader blokuje connection
                            SQLreader.Close();

                            await CheckActivationAsync(SQLConnection, configString);
                        }
                    }
                }

                _logger.Information($"Loaded {controllers.Count} machines. Starting controllers...");

                foreach (var controller in controllers)
                {
                    controller.StartProcess();
                }

                _logger.Information("Service fully initialized.");
            }
            catch (Exception e)
            {
                _logger.Error(e, "Fatal Error during Service Initialization");
            }
        }

        private async Task CheckActivationAsync(SqlConnection conn, string configString)
        {
            try
            {
                string configCRC32 = CRC32.ComputeHex(configString);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT [value] FROM dbo.configuration WHERE [option] = 'activation_code'";
                    var result = await cmd.ExecuteScalarAsync();
                    string activationCRC32 = result != DBNull.Value ? (string)result : "";

                    string activationComputeCRC32 = CRC32.ComputeHex(secretStart + ";" + configCRC32 + secretEnd);

                    Boolean activation = (activationCRC32 == activationComputeCRC32);

                    if (!activation) _logger.Warning("Service Activation Failed! CRC mismatch.");

                    // Update w bazie
                    cmd.CommandText = "EXEC configuration_update @p_config = @configCRC32, @p_activated = @activated";
                    cmd.Parameters.Clear();
                    cmd.Parameters.Add("@configCRC32", SqlDbType.VarChar, 8).Value = configCRC32;
                    cmd.Parameters.Add("@activated", SqlDbType.VarChar, 1).Value = activation ? "1" : "0";
                    await cmd.ExecuteNonQueryAsync();

                    // Ustawienie flagi w kontrolerach
                    foreach (var c in controllers) c.activation = activation;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Activation check error");
            }
        }

        private async Task<Boolean> CheckDatabaseConnectionAsync(CancellationToken token)
        {
            _logger.Information("Checking local database connection...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Używamy Connection Timeout w stringu, żeby nie czekać domyślnych 30s na próbę, tylko np. 5s
                    // To sprawia, że pętla jest bardziej responsywna
                    var connStr = $"Data source={DBLocalServer},{DBLocalPort};Initial Catalog={DBLocalDatabase};User ID={DBLocalUser};Password={DBLocalPassword};Connect Timeout=5;";

                    using (SqlConnection connection = new SqlConnection(connStr))
                    {
                        await connection.OpenAsync(token);
                        _logger.Information("Database connection established.");
                        return true;
                    }
                }
                catch (SqlException ex)
                {
                    _logger.Warning($"Database connection failed (SQL Error: {ex.Number}). Retrying in 15s... Service is waiting.");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unexpected DB Error during connection check.");
                }

                // OCZEKIWANIE (DELAY):
        // To jest kluczowe dla poprawnego zatrzymywania usługi.
        // Jeśli podczas tych 15 sekund klikniesz STOP, Delay rzuci OperationCanceledException i wyjdziemy z pętli.
        try
        {
            await Task.Delay(15000, token);
        }
        catch (OperationCanceledException)
        {
            // Usługa się zatrzymuje - wychodzimy z pętli
            break;
        }
            }
            return false;
        }

        // Metoda pomocnicza w MainService do tworzenia i uzupełniania obiektu
        private TemplatePreviousMachine CreatePreviousMachine(SqlTraceRepository repo, int id, short days, bool checkSec)
        {
            var pm = new TemplatePreviousMachine
            {
                MachineID = id,
                MaxDaysNumber = days,
                CheckSecondaryCode = checkSec
            };

            if (id > 0)
            {
                // Pobieramy konfigurację RAZ przy starcie
                // Uwaga: To wywołanie jest synchroniczne, ale przy starcie serwisu to akceptowalne.
                // Jeśli repozytorium jest thread-safe lub tworzymy nowe połączenie wewnątrz GetRemoteMachineConfig, to zadziała.
                // GetRemoteMachineConfig otwiera własne połączenie, więc jest OK.
                var config = repo.GetRemoteMachineConfig(id);
                if (config != null)
                {
                    pm.DbIp = config.Ip;
                    pm.DbUser = config.User;
                    pm.DbPassword = config.Password;
                }
            }
            return pm;
        }

    }
}
