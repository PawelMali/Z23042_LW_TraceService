using System;
using System.IO;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Data.SqlClient;
using Newtonsoft.Json;
using System.Data;
using Serilog;

namespace TraceService
{
    public partial class MainService : ServiceBase
    {
        private List<PLCController> controllers = new List<PLCController>();

        private readonly ILogger _logger;

        //Database
        private readonly String DBServer = Environment.GetEnvironmentVariable("DATABASE_SERVER");
        private readonly String DBPort = Environment.GetEnvironmentVariable("DATABASE_PORT");
        private readonly String DBDatabase = Environment.GetEnvironmentVariable("DATABASE_DATABASE");
        private readonly String DBUser = Environment.GetEnvironmentVariable("DATABASE_USER");
        private readonly String DBPassword = Environment.GetEnvironmentVariable("DATABASE_PASSWORD");
        private readonly Boolean Debug = Environment.GetEnvironmentVariable("DEBUG").ToUpper() == "TRUE" ? true : false;

        private readonly String secretStart = "Jz1wGOi8Ql33mHxEMwrmfZjqh95BDcrq";
        private readonly String secretEnd = "wLAD2KjXHDpaTLUI5MbvyafzaojE4107";

        MessageQueueService messageQueueService = new MessageQueueService();

        public MainService()
        {
            InitializeComponent();

            _logger = new LoggerConfiguration()
            .WriteTo.File(
                path: @"C:\Trace\service_event_.txt",
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} | {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();
        }

        protected override void OnStart(String[] args)
        {
            LogEvent("Version: 2025.12.1");
            messageQueueService.NetMQ_Start();
            Task.Run(() => messageQueueService.StartProcessingQueue());
            while (true)
            {
                if (messageQueueService.IsConnected)
                {
                    Task.Run(() => InitializeServiceAsync());
                    break;
                }
            }
        }

        protected override void OnStop()
        {
            foreach (var controller in controllers)
            {
                controller.StopProcess();
            }
            LogEvent("Service stopped.");
            messageQueueService.NetMQ_Stop();

            Environment.Exit(0);
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
        private async Task InitializeServiceAsync()
        {
            LogEvent("Checking database connection.");
            Boolean isConnected = await CheckDatabaseConnectionAsync();

            if (isConnected)
            {
                try
                {
                    SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                    await SQLConnection.OpenAsync();
                    SqlCommand SQLCommand = SQLConnection.CreateCommand();

                    SQLCommand.CommandText = "SELECT machines.machine_id, machines.machine_name, machines.plc_ip, machines.plc_type, machines.read_db, machines.write_db, machines.result_db, machines.numbers_of_parameters, machines.check_only_once, machines.dmc_other_line_active, machines.id_previous_machine_1, machines.max_days_number_1, machines.check_secondary_code_1, machines.id_previous_machine_2, machines.max_days_number_2, machines.check_secondary_code_2, machines.id_previous_machine_3, machines.max_days_number_3, machines.check_secondary_code_3, lines.database_ip, lines.database_login, lines.database_password FROM " +
                                                "(SELECT id_line, machine_id, machine_name, plc_ip, plc_type, read_db, write_db, result_db, numbers_of_parameters, check_only_once, dmc_other_line_active, id_previous_machine_1, max_days_number_1, check_secondary_code_1, id_previous_machine_2, max_days_number_2, check_secondary_code_2, id_previous_machine_3, max_days_number_3, check_secondary_code_3 FROM dbo.machines) machines " +
                                                "LEFT JOIN dbo.lines lines ON lines.id = machines.id_line " +
                                                "WHERE machines.plc_ip IS NOT NULL AND LEN(machines.plc_ip) > 0 " +
                                                "ORDER BY machines.plc_ip, machines.plc_type";

                    SqlDataReader SQLreader = await SQLCommand.ExecuteReaderAsync();

                    String configString = "";

                    while (await SQLreader.ReadAsync())
                    {
                        String plcIP = SQLreader.IsDBNull(2) ? null : SQLreader.GetString(2).Trim();
                        Byte plcType = SQLreader.IsDBNull(3) ? (Byte)1 : SQLreader.GetByte(3);
                        configString += plcIP.ToString() + ":" + plcType.ToString() + ";";

                        TemplatePreviousMachine previousMachine1 = new TemplatePreviousMachine { MachineID = SQLreader.IsDBNull(10) ? 0 : SQLreader.GetInt32(10), MaxDaysNumber = SQLreader.IsDBNull(11) ? (Int16)0 : SQLreader.GetInt16(11), CheckSecondaryCode = SQLreader.IsDBNull(12) ? false : SQLreader.GetBoolean(12) };
                        TemplatePreviousMachine previousMachine2 = new TemplatePreviousMachine { MachineID = SQLreader.IsDBNull(13) ? 0 : SQLreader.GetInt32(13), MaxDaysNumber = SQLreader.IsDBNull(14) ? (Int16)0 : SQLreader.GetInt16(14), CheckSecondaryCode = SQLreader.IsDBNull(15) ? false : SQLreader.GetBoolean(15) };
                        TemplatePreviousMachine previousMachine3 = new TemplatePreviousMachine { MachineID = SQLreader.IsDBNull(16) ? 0 : SQLreader.GetInt32(16), MaxDaysNumber = SQLreader.IsDBNull(17) ? (Int16)0 : SQLreader.GetInt16(17), CheckSecondaryCode = SQLreader.IsDBNull(18) ? false : SQLreader.GetBoolean(18) };

                        controllers.Add(new PLCController(
                            SQLreader.IsDBNull(0) ? null : SQLreader.GetString(0).Trim(),
                            SQLreader.IsDBNull(1) ? null : SQLreader.GetString(1).Trim(),
                            plcIP,
                            plcType,
                            SQLreader.IsDBNull(4) ? null : SQLreader.GetString(4).Trim(),
                            SQLreader.IsDBNull(5) ? null : SQLreader.GetString(5).Trim(),
                            SQLreader.IsDBNull(6) ? null : SQLreader.GetString(6).Trim(),
                            SQLreader.IsDBNull(7) ? (Byte)1 : SQLreader.GetByte(7),
                            SQLreader.IsDBNull(8) ? false : SQLreader.GetBoolean(8),
                            SQLreader.IsDBNull(9) ? false : SQLreader.GetBoolean(9),
                            previousMachine1,
                            previousMachine2,
                            previousMachine3,
                            SQLreader.IsDBNull(19) ? DBServer : SQLreader.GetString(19).Trim(),
                            SQLreader.IsDBNull(20) ? DBUser : SQLreader.GetString(20).Trim(),
                            SQLreader.IsDBNull(21) ? DBPassword : SQLreader.GetString(21).Trim()
                        ));
                    }
                    SQLreader.Close();

                    string configCRC32 = CRC32.ComputeHex(configString.ToString());

                    SQLCommand.CommandText = "SELECT [value] FROM dbo.configuration WHERE [option] = 'activation_code'";
                    SQLreader = await SQLCommand.ExecuteReaderAsync();
                    await SQLreader.ReadAsync();
                    string activationCRC32 = SQLreader.IsDBNull(0) ? "" : SQLreader.GetString(0);

                    SQLreader.Close();

                    Boolean activation = true;
                    if (activationCRC32 != CRC32.ComputeHex(secretStart.ToString() + ";" + configCRC32.ToString() + secretEnd.ToString()))
                    {
                        LogEvent("Activation not successful");
                        activation = true;
                    }

                    SQLCommand.CommandText = "EXEC configuration_update @p_config = @configCRC32, @p_activated = @activated";
                    SQLCommand.Parameters.Add("@configCRC32", SqlDbType.VarChar, 8).Value = configCRC32;
                    SQLCommand.Parameters.Add("@activated", SqlDbType.VarChar, 1).Value = activation ? "1" : "0";
                    SQLCommand.ExecuteNonQuery();

                    SQLConnection.Close();

                    LogEvent("Loading machines.");

                    foreach (var controller in controllers)
                    {
                        NetMQ_SendPLCList(controller, "Loaded controller");
                        controller.activation = activation;
                        controller.StartProcess(ControllerProcess);
                    }

                    LogEvent("Service started.");
                }
                catch (Exception e)
                {
                    LogEvent($"Error: {e.Message}");
                }
            }
            else
            {
                LogEvent("Database connection failed.");
            }
        }

        private async Task<Boolean> CheckDatabaseConnectionAsync()
        {
            while (true)
            {
                try
                {
                    using (SqlConnection connection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";"))
                    {
                        await connection.OpenAsync();
                        connection.Close();
                        LogEvent("Checking database connection - Success");
                        return true;
                    }
                }
                catch (SqlException)
                {
                    LogEvent("Checking database connection - Error");
                    await Task.Delay(5000);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private async Task ControllerProcess(PLCController controller, CancellationToken token)
        {
            Byte lastCheckLife = (Byte)DateTime.Now.Second;
            Int32 lastCheckOK = 0;

            while (!token.IsCancellationRequested)
            {
                //controller.CheckInDatabaseTEST();
                //continue;

                if (!controller.IsConnected)
                {
                    NetMQ_SendPLCList(controller, "Not connected to PLC");
                    await Task.Delay(2000, token);
                    controller.IsConnected = true;
                    continue;
                }

                try
                {
                    if (controller.Type == "Siemens")
                    {
                        if (lastCheckLife != DateTime.Now.Second)
                        {
                            controller.WriteToPLCLifeSiemens();
                            lastCheckLife = (Byte)DateTime.Now.Second;
                        }

                        if (!controller.activation)
                        {
                            NetMQ_SendPLCList(controller, "Not activated");
                            await Task.Delay(5000, token);
                            continue;
                        }

                        controller.ReadDataFromPLCSiemens();
                        controller.WriteDataFromPLCSiemens();

                        if (Debug)
                        {
                            controller.LogEvent($"Read: {controller.S7_data_Read.ToString()}");
                            controller.LogEvent($"Write: {controller.S7_data_Write.ToString()}");
                            if (controller.NumbersOfParameters == 1)
                            {
                                SiemensDataResultLong result = controller.ReadResultLongFromPLCSiemens();
                                controller.LogEvent($"Result: {result.ToString()}");
                            }
                            else
                            {
                                SiemensDataResultShort result = controller.ReadResultShortFromPLCSiemens();
                                controller.LogEvent($"Result: {result.ToString()}");
                            }
                        }

                        switch (controller.S7_data_Read.Task_Send_To_PC)
                        {
                            case 1:
                                if (controller.S7_data_Write.Task_Confirm_From_PC != 1)
                                {
                                    controller.CheckInDatabase();
                                    LogEvent($"{controller.ID} - {controller.Name}: Read Data");
                                    NetMQ_SendPLCList(controller, "Read Data");
                                }
                                controller.S7_data_Write.Task_Confirm_From_PC = 1;
                                NetMQ_SendPLCList(controller, "Read Data");
                                controller.WriteToPLCLifeSiemens();
                                break;

                            case 2:
                                if (controller.S7_data_Write.Task_Confirm_From_PC != 2)
                                {
                                    controller.ReadParametersAndSaveToDatabase();
                                    LogEvent($"{controller.ID} - {controller.Name}: Save Data");
                                    NetMQ_SendPLCList(controller, "Save Data");
                                }
                                controller.S7_data_Write.Task_Confirm_From_PC = 2;
                                NetMQ_SendPLCList(controller, "Save Data");
                                controller.WriteToPLCLifeSiemens();
                                break;

                            default:
                                controller.S7_data_Write.Part_Status = 0;
                                controller.S7_data_Write.Task_Confirm_From_PC = 0;
                                controller.S7_data_Write.Error_Status = 0;
                                controller.WriteToPLCLifeSiemens();
                                break;
                        }
                    } 
                    else
                    {
                        if (lastCheckLife != DateTime.Now.Second)
                        {
                            controller.WriteToPLCLifeAB();
                            lastCheckLife = (Byte)DateTime.Now.Second;
                        }

                        if (!controller.activation)
                        {
                            NetMQ_SendPLCList(controller, "Not activated");
                            await Task.Delay(5000, token);
                            continue;
                        }

                        controller.ReadDataFromPLCAB();
                        controller.WriteDataFromPLCAB();

                        if (Debug)
                        {
                            controller.LogEvent($"Read: {controller.AB_data_Read.ToString()}");
                            controller.LogEvent($"Write: {controller.AB_data_Write.ToString()}");
                            if (controller.NumbersOfParameters == 1)
                            {
                                ABDataResultLong result = controller.ReadResultLongFromPLCAB();
                                controller.LogEvent($"Result: {result.ToString()}");
                            }
                            else
                            {
                                ABDataResultShort result = controller.ReadResultShortFromPLCAB();
                                controller.LogEvent($"Result: {result.ToString()}");
                            }
                        }

                        switch (controller.AB_data_Read.Task_Send_To_PC)
                        {
                            case 1:
                                if (controller.AB_data_Write.Task_Confirm_From_PC != 1)
                                {
                                    controller.CheckInDatabase();
                                    LogEvent($"{controller.ID} - {controller.Name}: Read Data");
                                    NetMQ_SendPLCList(controller, "Read Data");
                                }
                                controller.AB_data_Write.Task_Confirm_From_PC = 1;
                                NetMQ_SendPLCList(controller, "Read Data");
                                controller.WriteToPLCLifeAB();
                                break;

                            case 2:
                                if (controller.AB_data_Write.Task_Confirm_From_PC != 2)
                                {
                                    controller.ReadParametersAndSaveToDatabase();
                                    LogEvent($"{controller.ID} - {controller.Name}: Save Data");
                                    NetMQ_SendPLCList(controller, "Save Data");
                                }
                                controller.AB_data_Write.Task_Confirm_From_PC = 2;
                                NetMQ_SendPLCList(controller, "Save Data");
                                controller.WriteToPLCLifeAB();
                                break;

                            default:
                                controller.AB_data_Write.Part_Status = 0;
                                controller.AB_data_Write.Task_Confirm_From_PC = 0;
                                controller.AB_data_Write.Error_Status = 0;
                                controller.WriteToPLCLifeAB();
                                break;
                        }
                    }

                    await Task.Delay(100, token);
                    lastCheckOK++;
                    if (lastCheckOK > 4)
                    {
                        LogEvent($"{controller.ID} - {controller.Name}: OK");
                        NetMQ_SendPLCList(controller, "OK");
                        lastCheckOK = 0;
                    }
                    
                }
                catch (Exception e)
                {
                    controller.ErrorCount++;
                    LogEvent($"{controller.ID} - {controller.Name} error {controller.ErrorCount}: {e.Message}");
                    NetMQ_SendPLCList(controller, "Error");
                    await Task.Delay(2000, token);
                }
            }
        }

        #region NETMQ

        private void NetMQ_SendPLCList(PLCController controller, String status)
        {
            Object readDataList = null;
            Object writeDataList = null;
            Object resultDataList = null;
            if (controller.IsConnected) {
                try
                {
                    if (controller.Type == "Siemens")
                    {
                        if (controller.NumbersOfParameters == 1)
                        {
                            resultDataList = NetMQ_ResultListLongSiemens(controller);
                        }
                        else
                        {
                            resultDataList = NetMQ_ResultListShortSiemens(controller);
                        }
                        readDataList = controller.S7_data_Read;
                        writeDataList = controller.S7_data_Write;
                    }
                    else
                    {
                        if (controller.NumbersOfParameters == 1)
                        {
                            resultDataList = NetMQ_ResultListLongAB(controller);
                        }
                        else
                        {
                            resultDataList = NetMQ_ResultListShortAB(controller);
                        }
                        readDataList = controller.AB_data_Read;
                        writeDataList = controller.AB_data_Write;
                    }
                }
                catch(Exception)
                {
                    readDataList = null;
                    writeDataList = null;
                    resultDataList = null;
                }
            }

            TemplateSendPLCList SendPLCList = new TemplateSendPLCList
            {
                MachineID = controller.ID,
                MachineName = controller.Name,
                Type = controller.Type,
                IP = controller.IP,
                IsRunning = controller.IsRunning,
                Status = status,
                MessageTime = DateTime.Now,
                readData = readDataList,
                writeData = writeDataList,
                resultData = resultDataList
            };

            String jsonString = JsonConvert.SerializeObject(SendPLCList, Formatting.None);
            if (!String.IsNullOrEmpty(jsonString))
            {
                messageQueueService.EnqueueMessage(jsonString);
            }
        }

        private TemplateResultLongDataList NetMQ_ResultListLongSiemens(PLCController controller)
        {
            SiemensDataResultLong result = controller.ReadResultLongFromPLCSiemens();

            TemplateResultLongDataList data = new TemplateResultLongDataList
            {
                DMC_Code1 = result.DMC_Code1.ToString(),
                DMC_Code2 = result.DMC_Code2.ToString(),
                Operation_Result1 = result.Operation_Result1,
                Operation_Result2 = result.Operation_Result2,
                Operation_DateTime1 = new DateTime(result.Operation_DateTime1.Year, result.Operation_DateTime1.Month, result.Operation_DateTime1.Day, result.Operation_DateTime1.Hour, result.Operation_DateTime1.Minute, result.Operation_DateTime1.Second),
                Operation_DateTime2 = new DateTime(result.Operation_DateTime2.Year, result.Operation_DateTime2.Month, result.Operation_DateTime2.Day, result.Operation_DateTime2.Hour, result.Operation_DateTime2.Minute, result.Operation_DateTime2.Second),
                Reference = result.Reference.ToString(),
                Cycle_Time = result.Cycle_Time,
                Operator = result.Operator.ToString(),
                int_1 = result.dints[0],
                int_2 = result.dints[1],
                int_3 = result.dints[2],
                int_4 = result.dints[3],
                int_5 = result.dints[4],
                int_6 = result.dints[5],
                int_7 = result.dints[6],
                int_8 = result.dints[7],
                int_9 = result.dints[8],
                int_10 = result.dints[9],
                real_1 = result.reals[0],
                real_2 = result.reals[1],
                real_3 = result.reals[2],
                real_4 = result.reals[3],
                real_5 = result.reals[4],
                real_6 = result.reals[5],
                real_7 = result.reals[6],
                real_8 = result.reals[7],
                real_9 = result.reals[8],
                real_10 = result.reals[9],
                real_11 = result.reals[10],
                real_12 = result.reals[11],
                real_13 = result.reals[12],
                real_14 = result.reals[13],
                real_15 = result.reals[14],
                real_16 = result.reals[15],
                real_17 = result.reals[16],
                real_18 = result.reals[17],
                real_19 = result.reals[18],
                real_20 = result.reals[19],
                real_21 = result.reals[20],
                real_22 = result.reals[21],
                real_23 = result.reals[22],
                real_24 = result.reals[23],
                real_25 = result.reals[24],
                real_26 = result.reals[25],
                real_27 = result.reals[26],
                real_28 = result.reals[27],
                real_29 = result.reals[28],
                real_30 = result.reals[29],
                real_31 = result.reals[30],
                real_32 = result.reals[31],
                real_33 = result.reals[32],
                real_34 = result.reals[33],
                real_35 = result.reals[34],
                real_36 = result.reals[35],
                real_37 = result.reals[36],
                real_38 = result.reals[37],
                real_39 = result.reals[38],
                real_40 = result.reals[39],
                real_41 = result.reals[40],
                real_42 = result.reals[41],
                real_43 = result.reals[42],
                real_44 = result.reals[43],
                real_45 = result.reals[44],
                real_46 = result.reals[45],
                real_47 = result.reals[46],
                real_48 = result.reals[47],
                real_49 = result.reals[48],
                real_50 = result.reals[49],
                real_51 = result.reals[50],
                real_52 = result.reals[51],
                real_53 = result.reals[52],
                real_54 = result.reals[53],
                real_55 = result.reals[54],
                real_56 = result.reals[55],
                real_57 = result.reals[56],
                real_58 = result.reals[57],
                real_59 = result.reals[58],
                real_60 = result.reals[59],
                real_61 = result.reals[60],
                real_62 = result.reals[61],
                real_63 = result.reals[62],
                real_64 = result.reals[63],
                real_65 = result.reals[64],
                real_66 = result.reals[65],
                real_67 = result.reals[66],
                real_68 = result.reals[67],
                real_69 = result.reals[68],
                real_70 = result.reals[69],
                real_71 = result.reals[70],
                real_72 = result.reals[71],
                real_73 = result.reals[72],
                real_74 = result.reals[73],
                real_75 = result.reals[74],
                real_76 = result.reals[75],
                real_77 = result.reals[76],
                real_78 = result.reals[77],
                real_79 = result.reals[78],
                real_80 = result.reals[79],
                real_81 = result.reals[80],
                real_82 = result.reals[81],
                real_83 = result.reals[82],
                real_84 = result.reals[83],
                real_85 = result.reals[84],
                real_86 = result.reals[85],
                real_87 = result.reals[86],
                real_88 = result.reals[87],
                real_89 = result.reals[88],
                real_90 = result.reals[89],
                real_91 = result.reals[90],
                real_92 = result.reals[91],
                real_93 = result.reals[92],
                real_94 = result.reals[93],
                real_95 = result.reals[94],
                real_96 = result.reals[95],
                real_97 = result.reals[96],
                real_98 = result.reals[97],
                real_99 = result.reals[98],
                real_100 = result.reals[99],
                dtl_1 = new DateTime(result.dtls[0].Year, result.dtls[0].Month, result.dtls[0].Day, result.dtls[0].Hour, result.dtls[0].Minute, result.dtls[0].Second),
                dtl_2 = new DateTime(result.dtls[1].Year, result.dtls[1].Month, result.dtls[1].Day, result.dtls[1].Hour, result.dtls[1].Minute, result.dtls[1].Second),
                dtl_3 = new DateTime(result.dtls[2].Year, result.dtls[2].Month, result.dtls[2].Day, result.dtls[2].Hour, result.dtls[2].Minute, result.dtls[2].Second),
                dtl_4 = new DateTime(result.dtls[3].Year, result.dtls[3].Month, result.dtls[3].Day, result.dtls[3].Hour, result.dtls[3].Minute, result.dtls[3].Second),
                dtl_5 = new DateTime(result.dtls[4].Year, result.dtls[4].Month, result.dtls[4].Day, result.dtls[4].Hour, result.dtls[4].Minute, result.dtls[4].Second),
                string_1 = result.strings[0].ToString(),
                string_2 = result.strings[1].ToString(),
                string_3 = result.strings[2].ToString(),
                string_4 = result.strings[3].ToString(),
                string_5 = result.strings[4].ToString(),
            };

            return data;
        }

        private TemplateResultShortDataList NetMQ_ResultListShortSiemens(PLCController controller)
        {
            SiemensDataResultShort result = controller.ReadResultShortFromPLCSiemens();

            TemplateResultShortDataList data = new TemplateResultShortDataList
            {
                DMC_Code1 = result.DMC_Code1.ToString(),
                DMC_Code2 = result.DMC_Code2.ToString(),
                Operation_Result1 = result.Operation_Result1,
                Operation_Result2 = result.Operation_Result2,
                Operation_DateTime1 = new DateTime(result.Operation_DateTime1.Year, result.Operation_DateTime1.Month, result.Operation_DateTime1.Day, result.Operation_DateTime1.Hour, result.Operation_DateTime1.Minute, result.Operation_DateTime1.Second),
                Operation_DateTime2 = new DateTime(result.Operation_DateTime2.Year, result.Operation_DateTime2.Month, result.Operation_DateTime2.Day, result.Operation_DateTime2.Hour, result.Operation_DateTime2.Minute, result.Operation_DateTime2.Second),
                Reference = result.Reference.ToString(),
                Cycle_Time = result.Cycle_Time,
                Operator = result.Operator.ToString(),
                int_1 = result.dints[0],
                int_2 = result.dints[1],
                int_3 = result.dints[2],
                int_4 = result.dints[3],
                int_5 = result.dints[4],
                int_6 = result.dints[5],
                int_7 = result.dints[6],
                int_8 = result.dints[7],
                int_9 = result.dints[8],
                int_10 = result.dints[9],
                real_1 = result.reals[0],
                real_2 = result.reals[1],
                real_3 = result.reals[2],
                real_4 = result.reals[3],
                real_5 = result.reals[4],
                real_6 = result.reals[5],
                real_7 = result.reals[6],
                real_8 = result.reals[7],
                real_9 = result.reals[8],
                real_10 = result.reals[9],
                dtl_1 = new DateTime(result.dtls[0].Year, result.dtls[0].Month, result.dtls[0].Day, result.dtls[0].Hour, result.dtls[0].Minute, result.dtls[0].Second),
                dtl_2 = new DateTime(result.dtls[1].Year, result.dtls[1].Month, result.dtls[1].Day, result.dtls[1].Hour, result.dtls[1].Minute, result.dtls[1].Second),
                dtl_3 = new DateTime(result.dtls[2].Year, result.dtls[2].Month, result.dtls[2].Day, result.dtls[2].Hour, result.dtls[2].Minute, result.dtls[2].Second),
                string_1 = result.strings[0].ToString(),
                string_2 = result.strings[1].ToString(),
                string_3 = result.strings[2].ToString(),
                string_4 = result.strings[3].ToString(),
                string_5 = result.strings[4].ToString(),
                string_6 = result.strings[5].ToString(),
                string_7 = result.strings[6].ToString(),
            };

            return data;
        }

        private TemplateResultLongDataList NetMQ_ResultListLongAB(PLCController controller)
        {
            ABDataResultLong result = controller.ReadResultLongFromPLCAB();

            TemplateResultLongDataList data = new TemplateResultLongDataList
            {
                DMC_Code1 = result.DMC_Code1.ToString(),
                DMC_Code2 = result.DMC_Code2.ToString(),
                Operation_Result1 = result.Operation_Result1,
                Operation_Result2 = result.Operation_Result2,
                Operation_DateTime1 = result.Operation_DateTime1.ToDateTime(),
                Operation_DateTime2 = result.Operation_DateTime2.ToDateTime(),
                Reference = result.Reference.ToString(),
                Cycle_Time = result.Cycle_Time,
                Operator = result.Operator.ToString(),
                int_1 = result.dints[0],
                int_2 = result.dints[1],
                int_3 = result.dints[2],
                int_4 = result.dints[3],
                int_5 = result.dints[4],
                int_6 = result.dints[5],
                int_7 = result.dints[6],
                int_8 = result.dints[7],
                int_9 = result.dints[8],
                int_10 = result.dints[9],
                real_1 = result.reals[0],
                real_2 = result.reals[1],
                real_3 = result.reals[2],
                real_4 = result.reals[3],
                real_5 = result.reals[4],
                real_6 = result.reals[5],
                real_7 = result.reals[6],
                real_8 = result.reals[7],
                real_9 = result.reals[8],
                real_10 = result.reals[9],
                real_11 = result.reals[10],
                real_12 = result.reals[11],
                real_13 = result.reals[12],
                real_14 = result.reals[13],
                real_15 = result.reals[14],
                real_16 = result.reals[15],
                real_17 = result.reals[16],
                real_18 = result.reals[17],
                real_19 = result.reals[18],
                real_20 = result.reals[19],
                real_21 = result.reals[20],
                real_22 = result.reals[21],
                real_23 = result.reals[22],
                real_24 = result.reals[23],
                real_25 = result.reals[24],
                real_26 = result.reals[25],
                real_27 = result.reals[26],
                real_28 = result.reals[27],
                real_29 = result.reals[28],
                real_30 = result.reals[29],
                real_31 = result.reals[30],
                real_32 = result.reals[31],
                real_33 = result.reals[32],
                real_34 = result.reals[33],
                real_35 = result.reals[34],
                real_36 = result.reals[35],
                real_37 = result.reals[36],
                real_38 = result.reals[37],
                real_39 = result.reals[38],
                real_40 = result.reals[39],
                real_41 = result.reals[40],
                real_42 = result.reals[41],
                real_43 = result.reals[42],
                real_44 = result.reals[43],
                real_45 = result.reals[44],
                real_46 = result.reals[45],
                real_47 = result.reals[46],
                real_48 = result.reals[47],
                real_49 = result.reals[48],
                real_50 = result.reals[49],
                real_51 = result.reals[50],
                real_52 = result.reals[51],
                real_53 = result.reals[52],
                real_54 = result.reals[53],
                real_55 = result.reals[54],
                real_56 = result.reals[55],
                real_57 = result.reals[56],
                real_58 = result.reals[57],
                real_59 = result.reals[58],
                real_60 = result.reals[59],
                real_61 = result.reals[60],
                real_62 = result.reals[61],
                real_63 = result.reals[62],
                real_64 = result.reals[63],
                real_65 = result.reals[64],
                real_66 = result.reals[65],
                real_67 = result.reals[66],
                real_68 = result.reals[67],
                real_69 = result.reals[68],
                real_70 = result.reals[69],
                real_71 = result.reals[70],
                real_72 = result.reals[71],
                real_73 = result.reals[72],
                real_74 = result.reals[73],
                real_75 = result.reals[74],
                real_76 = result.reals[75],
                real_77 = result.reals[76],
                real_78 = result.reals[77],
                real_79 = result.reals[78],
                real_80 = result.reals[79],
                real_81 = result.reals[80],
                real_82 = result.reals[81],
                real_83 = result.reals[82],
                real_84 = result.reals[83],
                real_85 = result.reals[84],
                real_86 = result.reals[85],
                real_87 = result.reals[86],
                real_88 = result.reals[87],
                real_89 = result.reals[88],
                real_90 = result.reals[89],
                real_91 = result.reals[90],
                real_92 = result.reals[91],
                real_93 = result.reals[92],
                real_94 = result.reals[93],
                real_95 = result.reals[94],
                real_96 = result.reals[95],
                real_97 = result.reals[96],
                real_98 = result.reals[97],
                real_99 = result.reals[98],
                real_100 = result.reals[99],
                dtl_1 = result.dtls[0].ToDateTime(),
                dtl_2 = result.dtls[1].ToDateTime(),
                dtl_3 = result.dtls[2].ToDateTime(),
                dtl_4 = result.dtls[3].ToDateTime(),
                dtl_5 = result.dtls[4].ToDateTime(),
                string_1 = result.strings[0].ToString(),
                string_2 = result.strings[1].ToString(),
                string_3 = result.strings[2].ToString(),
                string_4 = result.strings[3].ToString(),
                string_5 = result.strings[4].ToString(),
            };

            return data;
        }

        private TemplateResultShortDataList NetMQ_ResultListShortAB(PLCController controller)
        {
            ABDataResultShort result = controller.ReadResultShortFromPLCAB();

            TemplateResultShortDataList data = new TemplateResultShortDataList
            {
                DMC_Code1 = result.DMC_Code1.ToString(),
                DMC_Code2 = result.DMC_Code2.ToString(),
                Operation_Result1 = result.Operation_Result1,
                Operation_Result2 = result.Operation_Result2,
                Operation_DateTime1 = result.Operation_DateTime1.ToDateTime(),
                Operation_DateTime2 = result.Operation_DateTime2.ToDateTime(),
                Reference = result.Reference.ToString(),
                Cycle_Time = result.Cycle_Time,
                Operator = result.Operator.ToString(),
                int_1 = result.dints[0],
                int_2 = result.dints[1],
                int_3 = result.dints[2],
                int_4 = result.dints[3],
                int_5 = result.dints[4],
                int_6 = result.dints[5],
                int_7 = result.dints[6],
                int_8 = result.dints[7],
                int_9 = result.dints[8],
                int_10 = result.dints[9],
                real_1 = result.reals[0],
                real_2 = result.reals[1],
                real_3 = result.reals[2],
                real_4 = result.reals[3],
                real_5 = result.reals[4],
                real_6 = result.reals[5],
                real_7 = result.reals[6],
                real_8 = result.reals[7],
                real_9 = result.reals[8],
                real_10 = result.reals[9],
                dtl_1 = result.dtls[0].ToDateTime(),
                dtl_2 = result.dtls[1].ToDateTime(),
                dtl_3 = result.dtls[2].ToDateTime(),
                string_1 = result.strings[0].ToString(),
                string_2 = result.strings[1].ToString(),
                string_3 = result.strings[2].ToString(),
                string_4 = result.strings[3].ToString(),
                string_5 = result.strings[4].ToString(),
                string_6 = result.strings[5].ToString(),
                string_7 = result.strings[6].ToString(),
            };

            return data;
        }

        #endregion NETMQ

        #region LOGS

        private void LogEvent(String message)
        {
            _logger.Information(message);
        }

        #endregion LOGS
    }
}
