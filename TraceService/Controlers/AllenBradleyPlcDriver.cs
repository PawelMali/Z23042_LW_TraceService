using AutomatedSolutions.ASCommStd;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TraceService.Interfaces;
using TraceService.Models;
using ABLogix = AutomatedSolutions.ASCommStd.AB.Logix;

namespace TraceService.Controlers
{
    public class AllenBradleyPlcDriver : IPlcDriver
    {
        private readonly ILogger _logger;
        private readonly int _machineId;
        private readonly string _ipAddress;
        private readonly string _tagNameRead;
        private readonly string _tagNameWrite;
        private readonly string _tagNameResult;
        private readonly bool _isShortParam;

        private ABLogix.Net.Channel _channel;
        private ABLogix.Device _device;
        private ABLogix.Group _group;
        private ABLogix.Item _itemRead;
        private ABLogix.Item _itemWrite;
        private ABLogix.Item _itemResult;

        private ABDataRead _structRead;
        private ABDataWrite _structWrite;
        private object _structResult;

        private volatile bool _isConnected;
        public bool IsConnected => _isConnected;

        // --- SMART LOGGING & STABILITY ---
        private bool _isInErrorState = false; // Czy jesteśmy w trybie awarii?
        private readonly HashSet<string> _currentOutageErrors = new HashSet<string>(); // Unikalne błędy w tej awarii
        private DateTime _lastErrorTime = DateTime.MinValue; // Kiedy wystąpił ostatni błąd
        private DateTime _lastLogTime = DateTime.MinValue;   // Używane do interwału przypomnienia (1h)
        private DateTime _outageStartTime = DateTime.MinValue; // Kiedy zaczęła się awaria (do liczenia czasu trwania)

        // Konfiguracja błedów połaczenia
        private readonly TimeSpan _stabilityThreshold = TimeSpan.FromSeconds(10); // Czas bez błędów wymagany do uznania połączenia za stabilne
        private readonly TimeSpan _reminderInterval = TimeSpan.FromHours(1); // Co ile przypominać

        public AllenBradleyPlcDriver(ILogger logger, int machineId, string ip, string dbRead, string dbWrite, string dbResult, bool isShortParam)
        {
            _logger = logger;
            _machineId = machineId;
            _ipAddress = ip;
            _tagNameRead = dbRead;  // W AB to są nazwy tagów, nie DB number
            _tagNameWrite = dbWrite;
            _tagNameResult = dbResult;
            _isShortParam = isShortParam;

            _structRead = new ABDataRead();
            _structWrite = new ABDataWrite();

            if (_isShortParam) _structResult = new ABDataResultShort();
            else _structResult = new ABDataResultLong();

            try
            {
                // 1. INICJALIZACJA JEDNORAZOWA (w konstruktorze)
                _channel = new ABLogix.Net.Channel();
                _device = new ABLogix.Device(_ipAddress, ABLogix.Model.ControlLogix, 1000, 100);
                _group = new ABLogix.Group(false, 50);

                _itemRead = new ABLogix.Item { HWTagName = _tagNameRead, Label = "ItemDataRead", Elements = 1 };
                _itemWrite = new ABLogix.Item { HWTagName = _tagNameWrite, Label = "ItemDataWrite", Elements = 1 };
                _itemResult = new ABLogix.Item { HWTagName = _tagNameResult, Label = "ItemDataResult", Elements = 1 };

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

                _logger.Information($"Driver Initialized for Allen-Bradley PLC ID {_machineId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"FATAL: Allen-Bradley Driver Initialization Failed: {ex.Message}");
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
                    _logger.Information($"Connecting to Allen-Bradley PLC at {_ipAddress}...");

                // Reset struktur lokalnych (opcjonalne, ale bezpieczne)
                _structRead = new ABDataRead();
                _structWrite = new ABDataWrite();
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
            _logger.Information("Disconnected from AB PLC.");
        }

        public void WriteLifeBit()
        {
            if (!IsConnected) return;
            try
            {
                _structWrite.Life = (short)DateTime.Now.Second;
                _itemWrite.Write(_structWrite);
                ReportSuccess();
            }
            catch (Exception ex) 
            {
                HandleError(ex, "WriteLifeBit");
                _isConnected = false;
            }
        }

        public PlcControlState ReadControlState()
        {
            if (!IsConnected) return new PlcControlState();
            try
            {
                // 1. Odczyt bloku sterującego (Read)
                _itemRead.Read();
                _itemRead.GetStructuredValues(_structRead);

                ReportSuccess();

                return new PlcControlState
                {
                    TaskSendToPc = _structRead.Task_Send_To_PC,
                    // Zwracamy wartość z pamięci RAM (ostatnio zapisaną przez WriteStatus)
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
            if (!IsConnected) return;
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
            if (!IsConnected) throw new Exception("PLC Not Connected");
            try
            {
                _itemResult.Read();
                _itemResult.GetStructuredValues(_structResult);

                ReportSuccess();

                if (_isShortParam) return MapShortToModel((ABDataResultShort)_structResult);
                else return MapLongToModel((ABDataResultLong)_structResult);
            }
            catch (Exception ex)
            {
                HandleError(ex, "ReadTraceData");
                _isConnected = false;
                throw;
            }
        }

        private void HandleError(Exception ex, string context)
        {
            _lastErrorTime = DateTime.Now; // Zawsze aktualizujemy czas ostatniego błędu (dla stabilności)
            string errorMsg = ex.Message;

            // 1. Pierwsze wystąpienie awarii
            if (!_isInErrorState)
            {
                _isInErrorState = true;
                _outageStartTime = DateTime.Now;
                _currentOutageErrors.Clear();

                _logger.Error($"CONNECTION LOST (AB {_ipAddress}) [{context}]: {errorMsg}");
                _currentOutageErrors.Add(errorMsg);
                _lastLogTime = DateTime.Now; // Używane do interwału przypomnienia (1h)
                return;
            }

            // 2. Kolejne błędy - logujemy tylko jeśli treść jest inna niż dotychczasowe w tej awarii
            if (!_currentOutageErrors.Contains(errorMsg))
            {
                _logger.Error($"Additional Error (AB {_ipAddress}) [{context}]: {errorMsg}");
                _currentOutageErrors.Add(errorMsg);

                _lastLogTime = DateTime.Now;
                return;
            }

            // 3. PRZYPOMNIENIE (Heartbeat) - Jeśli minęła godzina od ostatniego wpisu w logu
            if ((DateTime.Now - _lastLogTime) > _reminderInterval)
            {
                double totalHours = (DateTime.Now - _outageStartTime).TotalHours;

                _logger.Warning($"Still Disconnected (AB {_ipAddress}) [{context}]. Downtime: {totalHours:F1}h. Last Error: {errorMsg}");

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
            // Jeśli system działa poprawnie i nie był w awarii -> nic nie rób
            if (!_isInErrorState) return;

            // Sprawdź czy minął czas stabilizacji (kwarantanna)
            if ((DateTime.Now - _lastErrorTime) > _stabilityThreshold)
            {
                _logger.Information($"CONNECTION RESTORED (AB {_ipAddress}) - Stable for {_stabilityThreshold.TotalSeconds}s");
                _isInErrorState = false;
                _currentOutageErrors.Clear();
            }
        }

        // Mappery AB (z użyciem .ToDateTime() itp.)
        private TraceLogModel MapLongToModel(ABDataResultLong source)
        {
            var model = new TraceLogModel
            {
                MachineId = _machineId,
                DmcCode1 = source.DMC_Code1.ToString(),
                DmcCode2 = source.DMC_Code2.ToString(),
                OperationResult1 = source.Operation_Result1,
                OperationResult2 = source.Operation_Result2,
                OperationDateTime1 = source.Operation_DateTime1.ToDateTime(),
                OperationDateTime2 = DateTime.Now,
                Reference = source.Reference.ToString(),
                CycleTime = source.Cycle_Time,
                Operator = source.Operator.ToString(),
                Ints = source.dints,
                Reals = source.reals
            };
            for (int i = 0; i < 5; i++)
            {
                model.Dtls[i] = source.dtls[i].ToDateTime();
                model.Strings[i] = source.strings[i].ToString();
            }
            return model;
        }

        private TraceLogModel MapShortToModel(ABDataResultShort source)
        {
            var model = new TraceLogModel
            {
                MachineId = _machineId,
                DmcCode1 = source.DMC_Code1.ToString(),
                DmcCode2 = source.DMC_Code2.ToString(),
                OperationResult1 = source.Operation_Result1,
                OperationResult2 = source.Operation_Result2,
                OperationDateTime1 = source.Operation_DateTime1.ToDateTime(),
                OperationDateTime2 = DateTime.Now,
                Reference = source.Reference.ToString(),
                CycleTime = source.Cycle_Time,
                Operator = source.Operator.ToString(),
                Ints = source.dints,
                Reals = source.reals
            };
            for (int i = 0; i < 3; i++) model.Dtls[i] = source.dtls[i].ToDateTime();
            for (int i = 0; i < 7; i++) model.Strings[i] = source.strings[i].ToString();
            return model;
        }

        public (object Read, object Write) GetControlStructures()
        {
            return (_structRead, _structWrite);
        }

        public object GetVisualisationObject()
        {
            if (!_isShortParam)
            {
                var src = (ABDataResultLong)_structResult;
                var dest = new TemplateResultLongDataList
                {
                    DMC_Code1 = src.DMC_Code1.ToString(),
                    DMC_Code2 = src.DMC_Code2.ToString(),
                    Operation_Result1 = src.Operation_Result1,
                    Operation_Result2 = src.Operation_Result2,
                    Operation_DateTime1 = src.Operation_DateTime1.ToDateTime(),
                    Operation_DateTime2 = src.Operation_DateTime2.ToDateTime(),
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

                    dtl_1 = src.dtls[0].ToDateTime(),
                    dtl_2 = src.dtls[1].ToDateTime(),
                    dtl_3 = src.dtls[2].ToDateTime(),
                    dtl_4 = src.dtls[3].ToDateTime(),
                    dtl_5 = src.dtls[4].ToDateTime(),

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
                var src = (ABDataResultShort)_structResult;
                var dest = new TemplateResultShortDataList
                {
                    DMC_Code1 = src.DMC_Code1.ToString(),
                    DMC_Code2 = src.DMC_Code2.ToString(),
                    Operation_Result1 = src.Operation_Result1,
                    Operation_Result2 = src.Operation_Result2,
                    Operation_DateTime1 = src.Operation_DateTime1.ToDateTime(),
                    Operation_DateTime2 = src.Operation_DateTime2.ToDateTime(),
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

                    dtl_1 = src.dtls[0].ToDateTime(),
                    dtl_2 = src.dtls[1].ToDateTime(),
                    dtl_3 = src.dtls[2].ToDateTime(),

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
                _logger.Information("Disposing AB Driver Resources...");
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
