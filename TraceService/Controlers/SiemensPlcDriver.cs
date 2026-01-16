using AutomatedSolutions.ASCommStd;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TraceService.Interfaces;
using TraceService.Models;
using SIS7 = AutomatedSolutions.ASCommStd.SI.S7;

namespace TraceService.Controlers
{
    public class SiemensPlcDriver : IPlcDriver
    {
        private readonly ILogger _logger;
        private readonly string _ipAddress;
        private readonly string _dbRead;
        private readonly string _dbWrite;
        private readonly string _dbResult;
        private readonly int _machineId; // Potrzebne do mapowania TraceLogModel
        private readonly bool _isShortParam; // Czy parametry są krótkie (2) czy długie (1)

        // Obiekty biblioteki ASComm
        private SIS7.Net.Channel _channel;
        private SIS7.Device _device;
        private SIS7.Group _group;
        private SIS7.Item _itemRead;
        private SIS7.Item _itemWrite;
        private SIS7.Item _itemResult;

        // Struktury danych
        private SiemensDataRead _structRead;
        private SiemensDataWrite _structWrite;
        // Obiekty rezultatów przechowujemy jako Object, rzutujemy przy odczycie
        private object _structResult;

        // Volatile nie jest konieczne przy jednym wątku kontrolera, ale bezpieczniej przy dostępie z Eventów
        private volatile bool _isConnected;
        public bool IsConnected => _isConnected;

        // --- POLA DO SMART LOGGINGU ---
        private bool _isInErrorState = false; // Czy jesteśmy w trybie awarii?
        private readonly HashSet<string> _currentErrors = new HashSet<string>(); // Unikalne błędy w tej awarii
        private DateTime _lastErrorTime = DateTime.MinValue; // Kiedy wystąpił ostatni błąd
        private DateTime _lastLogTime = DateTime.MinValue;   // Używane do interwału przypomnienia (1h)
        private DateTime _outageStartTime = DateTime.MinValue; // Kiedy zaczęła się awaria (do liczenia czasu trwania)

        // Konfiguracja błedów połaczenia
        private readonly TimeSpan _stabilityThreshold = TimeSpan.FromSeconds(10); // Czas bez błędów wymagany do uznania połączenia za stabilne
        private readonly TimeSpan _reminderInterval = TimeSpan.FromHours(1); // Co ile przypominać

        public SiemensPlcDriver(ILogger logger, int machineId, string ip, string dbRead, string dbWrite, string dbResult, bool isShortParam)
        {
            _logger = logger;
            _machineId = machineId;
            _ipAddress = ip;
            _dbRead = dbRead;
            _dbWrite = dbWrite;
            _dbResult = dbResult;
            _isShortParam = isShortParam;

            _structRead = new SiemensDataRead();
            _structWrite = new SiemensDataWrite();

            // Pre-alokacja struktur wyników
            if (_isShortParam)
                _structResult = new SiemensDataResultShort();
            else
                _structResult = new SiemensDataResultLong();

            try
            {
                // 1. INICJALIZACJA JEDNORAZOWA (w konstruktorze)
                _channel = new SIS7.Net.Channel();
                _device = new SIS7.Device(_ipAddress, SIS7.Model.S7_1200, 1000, 100) { Link = SIS7.LinkType.PC };
                _group = new SIS7.Group(false, 50);

                // Konfiguracja Itemów (zgodnie z oryginałem)

                // READ
                _itemRead = new SIS7.Item
                {
                    Label = "ItemDataRead",
                    HWTagName = $"{_dbRead}.DBB0",
                    Elements = 1,
                    HWDataType = SIS7.DataType.Structure,
                    StructureLength = _structRead.GetStructureLength()
                };

                // WRITE
                _itemWrite = new SIS7.Item
                {
                    Label = "ItemDataWrite",
                    HWTagName = $"{_dbWrite}.DBB0",
                    Elements = 1,
                    HWDataType = SIS7.DataType.Structure,
                    StructureLength = _structWrite.GetStructureLength()
                };

                // RESULT
                _itemResult = new SIS7.Item
                {
                    Label = "ItemDataResult",
                    HWTagName = $"{_dbResult}.DBB0",
                    Elements = 1,
                    HWDataType = SIS7.DataType.Structure
                };

                if (_isShortParam)
                    _itemResult.StructureLength = ((SiemensDataResultShort)_structResult).GetStructureLength();
                else
                    _itemResult.StructureLength = ((SiemensDataResultLong)_structResult).GetStructureLength();

                // Dodawanie do grupy
                _channel.Devices.Add(_device);
                _device.Groups.Add(_group);
                _group.Items.Add(_itemRead);
                _group.Items.Add(_itemWrite);
                _group.Items.Add(_itemResult);

                // Subskrypcja błędów asynchronicznych
                _channel.Error += (s, e) => HandleAsyncError("Channel", e.Message);
                _device.Error += (s, e) => HandleAsyncError("Device", e.Message);
                // Nie subskrybujemy Item.Error tutaj, bo i tak wyjdą przy Read/Write

                _logger.Information($"Driver Initialized for Siemens PLC ID {_machineId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"FATAL: Siemens Driver Initialization Failed: {ex.Message}");
                // Tutaj można rzucić wyjątek, bo bez tego sterownik jest martwy
                throw;
            }
        }

        public void Connect()
        {
            // Jeśli jesteśmy połączeni i stabilni, nic nie rób
            if (_isConnected && !_isInErrorState) return;

            try
            {
                if (!_isInErrorState)
                    _logger.Information($"Connecting to Siemens PLC at {_ipAddress}...");


                // Reset struktur lokalnych (opcjonalne, ale bezpieczne)
                _structRead = new SiemensDataRead();
                _structWrite = new SiemensDataWrite();
                _structWrite.Life = 0; // Reset Life bitu

                // WAŻNE: Wymuszamy próbę komunikacji, aby sprawdzić czy PLC żyje.
                // Jeśli to rzuci ChannelException, to trafi do catch na dole, 
                // a flaga _isConnected pozostanie false.
                // Czytamy najprostszy item (blok wejściowy), co jest bezpieczne.
                _itemRead.Read();

                // Dopiero jeśli powyższe przeszło bez błędu:
                _isConnected = true;

                ReportSuccess();
            }
            catch (Exception ex)
            {
                // Tutaj wpadną te ChannelException, ale teraz będą obsłużone
                // przez naszą logikę Smart Logging (nie będą spamować, jeśli to ten sam błąd)
                HandleError(ex, "Connect Validation");

                // Ważne: Upewniamy się, że flaga jest false, aby pętla główna 
                // weszła w tryb dłuższego oczekiwania (await Task.Delay(5000))
                _isConnected = false;
            }
        }

        public void Disconnect()
        {
            _isConnected = false;
            _isInErrorState = false;
            _logger.Information("Disconnected from Siemens PLC.");
        }

        public void WriteLifeBit()
        {
            if (!_isConnected) return;
            try
            {
                // Aktualizujemy tylko pole Life
                _structWrite.Life = (short)DateTime.Now.Second;
                _itemWrite.Write(_structWrite);
                ReportSuccess();
            }
            catch (Exception ex)
            {
                HandleError(ex, "WriteLifeBit");
                // Błąd zapisu zwykle oznacza zerwanie połączenia
                _isConnected = false;
            }
        }

        public PlcControlState ReadControlState()
        {
            if (!_isConnected) return new PlcControlState();

            try
            {
                // 1. Odczyt bloku sterującego (Read)
                _itemRead.Read();
                _itemRead.GetStructuredValues(_structRead);
                ReportSuccess();

                return new PlcControlState
                {
                    TaskSendToPc = _structRead.Task_Send_To_PC,
                    TaskConfirmFromPc = _structWrite.Task_Confirm_From_PC
                };
            }
            catch (Exception ex)
            {
                HandleError(ex, "ReadControlState");
                _isConnected = false;
                return new PlcControlState();
            }
        }

        public void WriteStatus(short partStatus, short errorStatus, short taskConfirm)
        {
            if (!_isConnected) return;

            try
            {
                _structWrite.Part_Status = partStatus;
                _structWrite.Error_Status = errorStatus;
                _structWrite.Task_Confirm_From_PC = taskConfirm;

                _itemWrite.Write(_structWrite);
                ReportSuccess();
            }
            catch (Exception ex)
            {
                HandleError(ex, "WriteStatus");
                _isConnected = false;
            }
        }

        public TraceLogModel ReadTraceData()
        {
            if (!_isConnected) throw new Exception("PLC Not Connected");

            try
            {
                _itemResult.Read();
                _itemResult.GetStructuredValues(_structResult);
                ReportSuccess();

                // Mapowanie wewnątrz sterownika - zwracamy czysty model
                if (_isShortParam)
                {
                    return MapShortToModel((SiemensDataResultShort)_structResult);
                }
                else
                {
                    return MapLongToModel((SiemensDataResultLong)_structResult);
                }
            }
            catch (Exception ex)
            {
                HandleError(ex, "ReadTraceData");
                _isConnected = false;
                throw;
            }
        }

        // --- Metody Pomocnicze (HandleError, Mapping) ---

        private void HandleError(Exception ex, string context)
        {
            _lastErrorTime = DateTime.Now; // Zawsze aktualizujemy czas ostatniego błędu (dla stabilności)
            string errorMsg = ex.Message;

            // 1. Wejście w stan awarii (pierwszy błąd)
            if (!_isInErrorState)
            {
                _isInErrorState = true;
                _outageStartTime = DateTime.Now;
                _currentErrors.Clear(); // Czyścimy listę z poprzedniej awarii

                _logger.Error($"CONNECTION LOST (Siemens {_ipAddress}) [{context}]: {errorMsg}");
                _currentErrors.Add(errorMsg);
                _lastLogTime = DateTime.Now; // Używane do interwału przypomnienia (1h)
                return;
            }

            // 2. Jeśli już jesteśmy w awarii - sprawdzamy czy to NOWY typ błędu
            if (!_currentErrors.Contains(errorMsg))
            {
                _logger.Error($"Additional Error (Siemens {_ipAddress}) [{context}]: {errorMsg}");
                _currentErrors.Add(errorMsg);

                _lastLogTime = DateTime.Now;
                return;
            }

            // 3. PRZYPOMNIENIE (Heartbeat) - Jeśli minęła godzina od ostatniego wpisu w logu
            if ((DateTime.Now - _lastLogTime) > _reminderInterval)
            {
                double totalHours = (DateTime.Now - _outageStartTime).TotalHours;

                _logger.Warning($"Still Disconnected (Siemens {_ipAddress}) [{context}]. Downtime: {totalHours:F1}h. Last Error: {errorMsg}");

                _lastLogTime = DateTime.Now;
            }
        }

        private void HandleAsyncError(string source, string message)
        {
            HandleError(new Exception(message), $"{source} Event");
            _isConnected = false;
        }

        private void ReportSuccess()
        {
            // Jeśli nie było awarii, nie robimy nic
            if (!_isInErrorState) return;

            // Jeśli jesteśmy w stanie awarii, sprawdzamy czy minął czas stabilizacji
            if ((DateTime.Now - _lastErrorTime) > _stabilityThreshold)
            {
                // Dopiero teraz uznajemy, że połączenie wróciło na dobre
                _logger.Information($"CONNECTION RESTORED (Siemens {_ipAddress}) - Stable for {_stabilityThreshold.TotalSeconds}s");

                _isInErrorState = false;
                _currentErrors.Clear();
            }
            // Jeśli czas nie minął -> CISZA. 
        }

        // Mapery (przeniesione z dawnego kontrolera i uproszczone)
        private TraceLogModel MapLongToModel(SiemensDataResultLong source)
        {
            var model = new TraceLogModel
            {
                MachineId = _machineId,
                DmcCode1 = source.DMC_Code1.ToString(),
                DmcCode2 = source.DMC_Code2.ToString(),
                OperationResult1 = source.Operation_Result1,
                OperationResult2 = source.Operation_Result2,
                OperationDateTime1 = MapDtl(source.Operation_DateTime1),
                OperationDateTime2 = DateTime.Now,
                Reference = source.Reference.ToString(),
                CycleTime = source.Cycle_Time,
                Operator = source.Operator.ToString(),
                Ints = source.dints,
                Reals = source.reals
            };

            for (int i = 0; i < 5; i++)
            {
                model.Dtls[i] = MapDtl(source.dtls[i]);
                model.Strings[i] = source.strings[i].ToString();
            }
            return model;
        }

        private TraceLogModel MapShortToModel(SiemensDataResultShort source)
        {
            var model = new TraceLogModel
            {
                MachineId = _machineId,
                DmcCode1 = source.DMC_Code1.ToString(),
                DmcCode2 = source.DMC_Code2.ToString(),
                OperationResult1 = source.Operation_Result1,
                OperationResult2 = source.Operation_Result2,
                OperationDateTime1 = MapDtl(source.Operation_DateTime1),
                OperationDateTime2 = DateTime.Now,
                Reference = source.Reference.ToString(),
                CycleTime = source.Cycle_Time,
                Operator = source.Operator.ToString(),
                Ints = source.dints,
                Reals = source.reals
            };

            for (int i = 0; i < 3; i++) model.Dtls[i] = MapDtl(source.dtls[i]);
            for (int i = 0; i < 7; i++) model.Strings[i] = source.strings[i].ToString();

            return model;
        }

        private DateTime MapDtl(AutomatedSolutions.ASCommStd.SI.S7.Data.DTL dtl)
        {
            // Walidacja przed rzuceniem wyjątku
            if (dtl.Year < 1970 || dtl.Year > 2100 ||
                dtl.Month < 1 || dtl.Month > 12 ||
                dtl.Day < 1 || dtl.Day > 31 ||
                dtl.Hour < 0 || dtl.Hour > 23 ||
                dtl.Minute < 0 || dtl.Minute > 59 ||
                dtl.Second < 0 || dtl.Second > 59)
            {
                return new DateTime(1970, 1, 1, 0, 0, 0);
            }

            try
            {
                return new DateTime(dtl.Year, dtl.Month, dtl.Day, dtl.Hour, dtl.Minute, dtl.Second);
            }
            catch
            {
                return new DateTime(1970, 1, 1, 0, 0, 0);
            }
        }

        public (object Read, object Write) GetControlStructures()
        {
            return (_structRead, _structWrite);
        }

        public object GetVisualisationObject()
        {
            if (!_isShortParam)
            {
                var src = (SiemensDataResultLong)_structResult;
                var dest = new TemplateResultLongDataList
                {
                    DMC_Code1 = src.DMC_Code1.ToString(),
                    DMC_Code2 = src.DMC_Code2.ToString(),
                    Operation_Result1 = src.Operation_Result1,
                    Operation_Result2 = src.Operation_Result2,
                    Operation_DateTime1 = MapDtl(src.Operation_DateTime1),
                    Operation_DateTime2 = MapDtl(src.Operation_DateTime2), 
                    Reference = src.Reference.ToString(),
                    Cycle_Time = src.Cycle_Time,
                    Operator = src.Operator.ToString(),

                    // Mapowanie tablic na pola
                    int_1 = src.dints[0],
                    int_2 = src.dints[1],
                    int_3 = src.dints[2],
                    int_4 = src.dints[3],
                    int_5 = src.dints[4],
                    int_6 = src.dints[5],
                    int_7 = src.dints[6],
                    int_8 = src.dints[7],
                    int_9 = src.dints[8],
                    int_10 = src.dints[9],

                    dtl_1 = MapDtl(src.dtls[0]),
                    dtl_2 = MapDtl(src.dtls[1]),
                    dtl_3 = MapDtl(src.dtls[2]),
                    dtl_4 = MapDtl(src.dtls[3]),
                    dtl_5 = MapDtl(src.dtls[4]),

                    string_1 = src.strings[0].ToString(),
                    string_2 = src.strings[1].ToString(),
                    string_3 = src.strings[2].ToString(),
                    string_4 = src.strings[3].ToString(),
                    string_5 = src.strings[4].ToString()
                };

                // Pętla do przepisania 100 Reali
                // Uwaga: TemplateResultLongDataList ma pola publiczne, więc użycie refleksji jest ok,
                var type = dest.GetType();
                for (int i = 0; i < 100; i++)
                {
                    type.GetField($"real_{i + 1}")?.SetValue(dest, src.reals[i]);
                }

                return dest;
            }
            else
            {
                var src = (SiemensDataResultShort)_structResult;
                var dest = new TemplateResultShortDataList
                {
                    DMC_Code1 = src.DMC_Code1.ToString(),
                    DMC_Code2 = src.DMC_Code2.ToString(),
                    Operation_Result1 = src.Operation_Result1,
                    Operation_Result2 = src.Operation_Result2,
                    Operation_DateTime1 = MapDtl(src.Operation_DateTime1),
                    Operation_DateTime2 = MapDtl(src.Operation_DateTime2),
                    Reference = src.Reference.ToString(),
                    Cycle_Time = src.Cycle_Time,
                    Operator = src.Operator.ToString(),

                    //// Mapowanie tablic na pola
                    int_1 = src.dints[0],
                    int_2 = src.dints[1],
                    int_3 = src.dints[2],
                    int_4 = src.dints[3],
                    int_5 = src.dints[4],
                    int_6 = src.dints[5],
                    int_7 = src.dints[6],
                    int_8 = src.dints[7],
                    int_9 = src.dints[8],
                    int_10 = src.dints[9],

                    dtl_1 = MapDtl(src.dtls[0]),
                    dtl_2 = MapDtl(src.dtls[1]),
                    dtl_3 = MapDtl(src.dtls[2]),

                    string_1 = src.strings[0].ToString(),
                    string_2 = src.strings[1].ToString(),
                    string_3 = src.strings[2].ToString(),
                    string_4 = src.strings[3].ToString(),
                    string_5 = src.strings[4].ToString(),
                    string_6 = src.strings[5].ToString(),
                    string_7 = src.strings[6].ToString()
                };

                var type = dest.GetType();
                for (int i = 0; i < 10; i++)
                {
                    type.GetField($"real_{i + 1}")?.SetValue(dest, src.reals[i]);
                }

                return dest;
            }
        }

        public void Dispose()
        {
            try
            {
                _logger.Information("Disposing Siemens Driver Resources...");
                if (_channel != null)
                {
                    _channel.Devices.Clear();
                    _channel.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error disposing driver: {ex.Message}");
            }
            GC.SuppressFinalize(this);
        }
    }
}
