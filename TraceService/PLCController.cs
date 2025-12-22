using AutomatedSolutions.ASCommStd;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TraceService.Enums;
using TraceService.Interfaces;
using TraceService.Models;
using TraceService.Controlers;
using ABLogix = AutomatedSolutions.ASCommStd.AB.Logix;
using SIS7 = AutomatedSolutions.ASCommStd.SI.S7;
using Serilog;

namespace TraceService
{
    public class PLCController
    {
        private readonly ITraceRepository _repository;


        private readonly ILogger _logger;
        public String ID { get; set; }
        public String Name { get; set; }
        public String IP { get; set; }
        public String Type { get; set; } // 1 - Siemens or 2 - Allen-Bradley

        public Boolean activation { get; set; }

        //Database
        private readonly String DBMasterServer = Environment.GetEnvironmentVariable("DATABASE_SERVER");
        private readonly String DBMasterPort = Environment.GetEnvironmentVariable("DATABASE_PORT");
        private readonly String DBMasterDatabase = Environment.GetEnvironmentVariable("DATABASE_DATABASE");
        private readonly String DBMasterUser = Environment.GetEnvironmentVariable("DATABASE_USER");
        private readonly String DBMasterPassword = Environment.GetEnvironmentVariable("DATABASE_PASSWORD");

        public String DBServer = Environment.GetEnvironmentVariable("DATABASE_SERVER");
        private readonly String DBPort = Environment.GetEnvironmentVariable("DATABASE_PORT");
        private readonly String DBDatabase = Environment.GetEnvironmentVariable("DATABASE_DATABASE");
        public String DBUser = Environment.GetEnvironmentVariable("DATABASE_USER");
        public String DBPassword = Environment.GetEnvironmentVariable("DATABASE_PASSWORD");

        private String DBRead = null;
        private String DBWrite = null;
        private String DBResult = null;
        public Byte NumbersOfParameters { get; private set; }
        public Boolean CheckOnlyOnce { get; private set; }
        public Boolean SaveToDiffrentDB { get; private set; }
        public TemplatePreviousMachine PreviousMachine1 { get; private set; }
        public TemplatePreviousMachine PreviousMachine2 { get; private set; }
        public TemplatePreviousMachine PreviousMachine3 { get; private set; }

        //SIEMENS
        private SIS7.Net.Channel S7_Channel = null;
        private SIS7.Device S7_Device = null;
        private SIS7.Group S7_Group = null;
        private SIS7.Item S7_ItemTemp = null;
        private SIS7.Item S7_Item_Data_Read = null;
        private SIS7.Item S7_Item_Data_Write = null;
        private SIS7.Item S7_Item_Data_ResultLong = null;
        private SIS7.Item S7_Item_Data_ResultShort = null;

        private Object S7_struct_Read = null;
        private Object S7_struct_Write = null;
        private Object S7_struct_ResultLong = null;
        private Object S7_struct_ResultShort = null;

        public SiemensDataRead S7_data_Read = null;
        public SiemensDataWrite S7_data_Write = null;
        public SiemensDataResultLong S7_data_ResultLong = null;
        public SiemensDataResultShort S7_data_ResultShort = null;

        //Allen-Bradley
        private ABLogix.Net.Channel AB_Channel = null;
        private ABLogix.Device AB_Device = null;
        private ABLogix.Group AB_Group = null;
        private ABLogix.Item AB_ItemTemp = null;
        private ABLogix.Item AB_Item_Data_Read = null;
        private ABLogix.Item AB_Item_Data_Write = null;
        private ABLogix.Item AB_Item_Data_ResultLong = null;
        private ABLogix.Item AB_Item_Data_ResultShort = null;

        private Object AB_struct_Read = null;
        private Object AB_struct_Write = null;
        private Object AB_struct_ResultLong = null;
        private Object AB_struct_ResultShort = null;

        public ABDataRead AB_data_Read = null;
        public ABDataWrite AB_data_Write = null;
        public ABDataResultLong AB_data_ResultLong = null;
        public ABDataResultShort AB_data_ResultShort = null;

        public Task ProcessTask { get; private set; }
        public Boolean IsRunning { get; private set; }
        public Boolean IsConnected { get; set; }
        public CancellationTokenSource ProcessTokenSource { get; private set; }
        public Int32 ErrorCount = 0;

        public PLCController(String id, String name, String ip, Byte type, String dbread, String dbwrite, String dbresult, Byte numbersofparameters, Boolean checkonlyonce, Boolean savetodiffrentdb, TemplatePreviousMachine previousmachine1, TemplatePreviousMachine previousmachine2, TemplatePreviousMachine previousmachine3, String dbserver, String dblogin, String dbpassword)
        {
            IsConnected = false;

            ID = id;
            Name = name;
            IP = ip;

            DBRead = dbread;
            DBWrite = dbwrite;
            DBResult = dbresult;
            NumbersOfParameters = numbersofparameters;
            CheckOnlyOnce = checkonlyonce;
            SaveToDiffrentDB = savetodiffrentdb;

            PreviousMachine1 = previousmachine1;
            PreviousMachine2 = previousmachine2;
            PreviousMachine3 = previousmachine3;

            _logger = new LoggerConfiguration()
            .WriteTo.File(
                path: $@"C:\Trace\log_{id}_.txt",
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} | {Message:lj}{NewLine}{Exception}",
                shared: true // Pozwala innym procesom czytać plik
            )
            .CreateLogger();

            if (type == 1)
            {
                Type = "Siemens";
            }
            else
            {
                Type = "Allen-Bradley";
            }

            if (dbserver != null && dbserver.Length > 0)
            {
                DBServer = dbserver;
            }

            if (dblogin != null && dblogin.Length > 0)
            {
                DBUser = dblogin;
            }

            if (dbpassword != null && dbpassword.Length > 0)
            {
                DBPassword = dbpassword;
            }

            ProcessTokenSource = new CancellationTokenSource();

            if (!String.IsNullOrEmpty(DBRead) &&
                !String.IsNullOrEmpty(DBWrite) &&
                !String.IsNullOrEmpty(DBResult))
            {
                if (Type == "Siemens")
                {
                    ConnectToPLCSiemens();
                }
                else
                {
                    ConnectToPLCAB();
                }
            }

            _repository = new SqlTraceRepository(DBServer, DBPort, DBDatabase, DBUser, DBPassword);
        }

        public void StartProcess(Func<PLCController, CancellationToken, Task> process)
        {
            LogEvent("START PLC PROCESS");
            IsRunning = true;
            ProcessTask = Task.Run(() => process(this, ProcessTokenSource.Token), ProcessTokenSource.Token);
        }

        public void StopProcess()
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            LogEvent("STOP PLC PROCESS");
            ProcessTokenSource.Cancel();
            try
            {
                Task.WhenAny(ProcessTask, Task.Delay(TimeSpan.FromSeconds(30))).Wait();
            }
            catch (AggregateException e)
            {
                foreach (var innerE in e.InnerExceptions)
                {
                    if (innerE is TaskCanceledException)
                    {
                        LogEvent("Task restart canceled: " + innerE.Message);
                    }
                    else
                    {
                        LogEvent("Task restart error: " + innerE.Message);
                    }
                }
            }

            if (S7_Channel != null)
            {
                S7_Channel.Error -= Channel_Error;
                S7_Device.Error -= Device_Error;
                S7_ItemTemp.Error -= Item_Error;
                S7_ItemTemp.DataChanged -= Item_DataChanged_S7;
                S7_Item_Data_Read.Error -= Item_Error;
                S7_Item_Data_Read.DataChanged -= Item_DataChanged_S7;
                S7_Item_Data_Write.Error -= Item_Error;
                S7_Item_Data_Write.DataChanged -= Item_DataChanged_S7;

                if (NumbersOfParameters == 1)
                {
                    S7_Item_Data_ResultLong.Error -= Item_Error;
                    S7_Item_Data_ResultLong.DataChanged -= Item_DataChanged_S7;
                } 
                else if (NumbersOfParameters == 2)
                {
                    S7_Item_Data_ResultShort.Error -= Item_Error;
                    S7_Item_Data_ResultShort.DataChanged -= Item_DataChanged_S7;
                }

                S7_Channel.Dispose();
                LogEvent("STOP PLC PROCESS - PLC DISPOSE");
            }
            
            if (AB_Channel != null)
            {
                AB_Channel.Error -= Channel_Error;
                AB_Device.Error -= Device_Error;
                AB_ItemTemp.Error -= Item_Error;
                AB_ItemTemp.DataChanged -= Item_DataChanged_AB;
                AB_Item_Data_Read.Error -= Item_Error;
                AB_Item_Data_Read.DataChanged -= Item_DataChanged_AB;
                AB_Item_Data_Write.Error -= Item_Error;
                AB_Item_Data_Write.DataChanged -= Item_DataChanged_AB;

                if (NumbersOfParameters == 1)
                {
                    AB_Item_Data_ResultLong.Error -= Item_Error;
                    AB_Item_Data_ResultLong.DataChanged -= Item_DataChanged_AB;
                }
                else if (NumbersOfParameters == 2)
                {
                    AB_Item_Data_ResultShort.Error -= Item_Error;
                    AB_Item_Data_ResultShort.DataChanged -= Item_DataChanged_AB;
                }

                AB_Channel.Dispose();
                LogEvent("STOP PLC PROCESS - PLC DISPOSE");
            }

            ProcessTask.Dispose();
            LogEvent("STOP PLC PROCESS - TASK DISPOSE");
        }

        public void LogEvent(String message)
        {
            _logger.Information(message);
        }

        #region ERRORS

        private void Channel_Error(Object sender, ChannelEventArgs e)
        {
            LogEvent("Channel.Error event fired: " + e.Message);
            ErrorCount++;
        }

        private void Device_Error(Object sender, DeviceEventArgs e)
        {
            LogEvent("Device.Error event fired: " + e.Message);
            ErrorCount++;
        }

        private void Item_Error(Object sender, ItemEventArgs e)
        {
            LogEvent("Item.Error event fired: " + e.Message);
            ErrorCount++;
        }

        private void Item_DataChanged_S7(Object sender, ItemDataChangedEventArgs e)
        {
            var theItem = (SIS7.Item)sender;
            if (theItem.Quality != Quality.GOOD)
            {
                ErrorCount++;
                LogEvent("Item.DataChange event fired, quality is " + theItem.Quality);
            }
        }

        private void Item_DataChanged_AB(Object sender, ItemDataChangedEventArgs e)
        {
            var theItem = (ABLogix.Item)sender;
            if (theItem.Quality != Quality.GOOD)
            {
                ErrorCount++;
                LogEvent("Item.DataChange event fired, quality is " + theItem.Quality);
            }
        }

        #endregion ERRORS

        #region SIEMENS-CONNECT

        private void ConnectToPLCSiemens()
        {
            LogEvent("ConnectToPLCSiemens | START");

            S7_Channel = new SIS7.Net.Channel();
            S7_Device = new SIS7.Device(IP, SIS7.Model.S7_1200, 1000, 100);
            S7_Device.Link = SIS7.LinkType.PC;
            S7_Group = new SIS7.Group(false, 50);
            S7_ItemTemp = new SIS7.Item() { Label = "ItemTemp" };

            S7_struct_Read = new SiemensDataRead();
            S7_struct_Write = new SiemensDataWrite();

            S7_data_Read = new SiemensDataRead();
            S7_data_Write = new SiemensDataWrite();

            S7_Item_Data_Read = new SIS7.Item() { Label = "ItemDataRead" };
            S7_Item_Data_Read.HWTagName = $"{DBRead}.DBB0";
            S7_Item_Data_Read.Elements = 1;
            S7_Item_Data_Read.HWDataType = SIS7.DataType.Structure;
            S7_Item_Data_Read.StructureLength = ((SiemensDataRead)(S7_struct_Read)).GetStructureLength();

            S7_Item_Data_Write = new SIS7.Item() { Label = "ItemDataWrite" };
            S7_Item_Data_Write.HWTagName = $"{DBWrite}.DBB0";
            S7_Item_Data_Write.Elements = 1;
            S7_Item_Data_Write.HWDataType = SIS7.DataType.Structure;
            S7_Item_Data_Write.StructureLength = ((SiemensDataWrite)(S7_struct_Write)).GetStructureLength();

            S7_Channel.Error += Channel_Error;
            S7_Device.Error += Device_Error;
            S7_ItemTemp.Error += Item_Error;
            S7_ItemTemp.DataChanged += Item_DataChanged_S7;
            S7_Item_Data_Read.Error += Item_Error;
            S7_Item_Data_Read.DataChanged += Item_DataChanged_S7;
            S7_Item_Data_Write.Error += Item_Error;
            S7_Item_Data_Write.DataChanged += Item_DataChanged_S7;

            if (NumbersOfParameters == 1)
            {
                S7_struct_ResultLong = new SiemensDataResultLong();
                S7_data_ResultLong = new SiemensDataResultLong();

                S7_Item_Data_ResultLong = new SIS7.Item() { Label = "ItemDataResult" };
                S7_Item_Data_ResultLong.HWTagName = $"{DBResult}.DBB0";
                S7_Item_Data_ResultLong.Elements = 1;
                S7_Item_Data_ResultLong.HWDataType = SIS7.DataType.Structure;
                S7_Item_Data_ResultLong.StructureLength = ((SiemensDataResultLong)(S7_struct_ResultLong)).GetStructureLength();

                S7_Item_Data_ResultLong.Error += Item_Error;
                S7_Item_Data_ResultLong.DataChanged += Item_DataChanged_S7;
            }
            else if (NumbersOfParameters == 2)
            {
                S7_struct_ResultShort = new SiemensDataResultShort();
                S7_data_ResultShort = new SiemensDataResultShort();

                S7_Item_Data_ResultShort = new SIS7.Item() { Label = "ItemDataResult" };
                S7_Item_Data_ResultShort.HWTagName = $"{DBResult}.DBB0";
                S7_Item_Data_ResultShort.Elements = 1;
                S7_Item_Data_ResultShort.HWDataType = SIS7.DataType.Structure;
                S7_Item_Data_ResultShort.StructureLength = ((SiemensDataResultShort)(S7_struct_ResultShort)).GetStructureLength();

                S7_Item_Data_ResultShort.Error += Item_Error;
                S7_Item_Data_ResultShort.DataChanged += Item_DataChanged_S7;
            }
            
            S7_Channel.Devices.Add(S7_Device);
            S7_Device.Groups.Add(S7_Group);
            S7_Group.Items.Add(S7_ItemTemp);
            S7_Group.Items.Add(S7_Item_Data_Read);
            S7_Group.Items.Add(S7_Item_Data_Write);

            if (NumbersOfParameters == 1)
            {
                S7_Group.Items.Add(S7_Item_Data_ResultLong);
            }
            else if (NumbersOfParameters == 2)
            {
                S7_Group.Items.Add(S7_Item_Data_ResultShort);
            }

            LogEvent("ConnectToPLCSiemens | END");
            IsConnected = true;
        }

        #endregion SIEMENS-CONNECT

        #region AB-CONNECT

        private void ConnectToPLCAB()
        {
            LogEvent("ConnectToPLCAB | START");

            AB_Channel = new ABLogix.Net.Channel();
            AB_Device = new ABLogix.Device(IP, ABLogix.Model.ControlLogix, 1000, 100);
            AB_Group = new ABLogix.Group(false, 50);
            AB_ItemTemp = new ABLogix.Item() { Label = "ItemTemp" };

            AB_struct_Read = new ABDataRead();
            AB_struct_Write = new ABDataWrite();

            AB_data_Read = new ABDataRead();
            AB_data_Write = new ABDataWrite();

            AB_Item_Data_Read = new ABLogix.Item() { Label = "ItemDataRead" };
            AB_Item_Data_Read.HWTagName = DBRead;
            AB_Item_Data_Read.Elements = 1;

            AB_Item_Data_Write = new ABLogix.Item() { Label = "ItemDataWrite" };
            AB_Item_Data_Write.HWTagName = DBWrite;
            AB_Item_Data_Write.Elements = 1;

            AB_Channel.Error += Channel_Error;
            AB_Device.Error += Device_Error;
            AB_ItemTemp.Error += Item_Error;
            AB_ItemTemp.DataChanged += Item_DataChanged_AB;
            AB_Item_Data_Read.Error += Item_Error;
            AB_Item_Data_Read.DataChanged += Item_DataChanged_AB;
            AB_Item_Data_Write.Error += Item_Error;
            AB_Item_Data_Write.DataChanged += Item_DataChanged_AB;
            
            if (NumbersOfParameters == 1)
            {
                AB_struct_ResultLong = new ABDataResultLong();
                AB_data_ResultLong = new ABDataResultLong();

                AB_Item_Data_ResultLong = new ABLogix.Item() { Label = "ItemDataResultLong" };
                AB_Item_Data_ResultLong.HWTagName = DBResult;
                AB_Item_Data_ResultLong.Elements = 1;

                AB_Item_Data_ResultLong.Error += Item_Error;
                AB_Item_Data_ResultLong.DataChanged += Item_DataChanged_AB;
            }
            else if (NumbersOfParameters == 2)
            {
                AB_struct_ResultShort = new ABDataResultShort();
                AB_data_ResultShort = new ABDataResultShort();

                AB_Item_Data_ResultShort = new ABLogix.Item() { Label = "ItemDataResultShort" };
                AB_Item_Data_ResultShort.HWTagName = DBResult;
                AB_Item_Data_ResultShort.Elements = 1;

                AB_Item_Data_ResultShort.Error += Item_Error;
                AB_Item_Data_ResultShort.DataChanged += Item_DataChanged_AB;
            }

            AB_Channel.Devices.Add(AB_Device);
            AB_Device.Groups.Add(AB_Group);
            AB_Group.Items.Add(AB_ItemTemp);
            AB_Group.Items.Add(AB_Item_Data_Read);
            AB_Group.Items.Add(AB_Item_Data_Write);

            if (NumbersOfParameters == 1)
            {
                AB_Group.Items.Add(AB_Item_Data_ResultLong);
            }
            else if (NumbersOfParameters == 2)
            {
                AB_Group.Items.Add(AB_Item_Data_ResultShort);
            }

            LogEvent("ConnectToPLCAB | END");
            IsConnected = true;
        }

        #endregion AB-CONNECT

        #region SIEMENS-DATA

        public SiemensDataRead ReadDataFromPLCSiemens()
        {
            try
            {
                S7_Item_Data_Read.Read();
                S7_Item_Data_Read.GetStructuredValues(S7_struct_Read);
                S7_data_Read = (SiemensDataRead)S7_struct_Read;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return S7_data_Read;
        }

        public SiemensDataWrite WriteDataFromPLCSiemens()
        {
            try
            {
                S7_Item_Data_Write.Read();
                S7_Item_Data_Write.GetStructuredValues(S7_struct_Write);
                S7_data_Write = (SiemensDataWrite)S7_struct_Write;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return S7_data_Write;
        }

        public SiemensDataResultLong ReadResultLongFromPLCSiemens()
        {
            try
            {
                S7_Item_Data_ResultLong.Read();
                S7_Item_Data_ResultLong.GetStructuredValues(S7_struct_ResultLong);
                S7_data_ResultLong = (SiemensDataResultLong)S7_struct_ResultLong;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return S7_data_ResultLong;
        }

        public SiemensDataResultShort ReadResultShortFromPLCSiemens()
        {
            try
            {
                S7_Item_Data_ResultShort.Read();
                S7_Item_Data_ResultShort.GetStructuredValues(S7_struct_ResultShort);
                S7_data_ResultShort = (SiemensDataResultShort)S7_struct_ResultShort;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return S7_data_ResultShort;
        }

        public void WriteToPLCLifeSiemens()
        {
            S7_data_Write.Life = (short)DateTime.Now.Second;
            S7_struct_Write = S7_data_Write;
            S7_Item_Data_Write.Write(S7_struct_Write);
        }

        #endregion SIEMENS-DATA

        #region AB-DATA

        public ABDataRead ReadDataFromPLCAB()
        {
            try
            {
                AB_Item_Data_Read.Read();
                AB_Item_Data_Read.GetStructuredValues(AB_struct_Read);
                AB_data_Read = (ABDataRead)AB_struct_Read;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return AB_data_Read;
        }

        public ABDataWrite WriteDataFromPLCAB()
        {
            try
            {
                AB_Item_Data_Write.Read();
                AB_Item_Data_Write.GetStructuredValues(AB_struct_Write);
                AB_data_Write = (ABDataWrite)AB_struct_Write;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return AB_data_Write;
        }

        public ABDataResultLong ReadResultLongFromPLCAB()
        {
            try
            {
                AB_Item_Data_ResultLong.Read();
                AB_Item_Data_ResultLong.GetStructuredValues(AB_struct_ResultLong);
                AB_data_ResultLong = (ABDataResultLong)AB_struct_ResultLong;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return AB_data_ResultLong;
        }

        public ABDataResultShort ReadResultShortFromPLCAB()
        {
            try
            {
                AB_Item_Data_ResultShort.Read();
                AB_Item_Data_ResultShort.GetStructuredValues(AB_struct_ResultShort);
                AB_data_ResultShort = (ABDataResultShort)AB_struct_ResultShort;
            }
            catch (SocketException eSocket)
            {
                LogEvent(eSocket.ToString());
                IsConnected = false;
            }
            catch (ChannelException eChannel)
            {
                LogEvent(eChannel.ToString());
            }
            catch (DeviceException eDevice)
            {
                LogEvent(eDevice.ToString());
            }
            catch (Exception e)
            {
                LogEvent(e.ToString());
                IsConnected = false;
            }
            return AB_data_ResultShort;
        }

        public void WriteToPLCLifeAB()
        {
            AB_data_Write.Life = (short)DateTime.Now.Second;
            AB_struct_Write = AB_data_Write;
            AB_Item_Data_Write.Write(AB_struct_Write);
        }

        #endregion AB-DATA

        #region CHECK-IN-DATABASE

        public void CheckInDatabase()
        {
            if (Type == "Siemens")
            {
                CheckInDatabaseSiemens();
            }
            else
            {
                CheckInDatabaseAB();
            }
        }

        #region CHECK-IN-DATABASE-SIEMENS

        private void CheckInDatabaseSiemens()
        {
            LogEvent("CheckInDatabaseSiemens | START");
            try
            {
                // --- A. Pobranie DMC ---
                string dmc1, dmc2;
                if (NumbersOfParameters == 1)
                {
                    var res = ReadResultLongFromPLCSiemens();
                    dmc1 = res.DMC_Code1.ToString();
                    dmc2 = res.DMC_Code2.ToString();
                }
                else
                {
                    var res = ReadResultShortFromPLCSiemens();
                    dmc1 = res.DMC_Code1.ToString();
                    dmc2 = res.DMC_Code2.ToString();
                }

                if (string.IsNullOrEmpty(dmc1) && string.IsNullOrEmpty(dmc2))
                {
                    SetSiemensStatus(2, (short)TraceErrorStatus.NoDmcCode);
                    return;
                }

                // --- B. Sprawdzenie lokalne (Scrap/CheckOnce) ---

                // 1. Czy ten detal jest już ZŁOMEM w naszej bazie?
                if (_repository.IsScrapDetected(dmc1, dmc2))
                {
                    LogEvent("CheckInDatabaseSiemens | LOCAL SCRAP DETECTED");
                    SetSiemensStatus(2, (short)TraceErrorStatus.ScrapDetected);
                    return;
                }

                // 2. Czy już tu był?
                if (CheckOnlyOnce && _repository.IsDmcProcessed(int.Parse(ID), dmc1, dmc2))
                {
                    SetSiemensStatus(2, (short)TraceErrorStatus.AlreadyProcessed);
                    return;
                }

                // --- C. Sprawdzenie Poprzednich Maszyn ---
                var machines = new List<TemplatePreviousMachine>();
                if (PreviousMachine1.MachineID > 0) machines.Add(PreviousMachine1);
                if (PreviousMachine2.MachineID > 0) machines.Add(PreviousMachine2);
                if (PreviousMachine3.MachineID > 0) machines.Add(PreviousMachine3);

                if (machines.Count == 0)
                {
                    SetSiemensStatus(1, (short)TraceErrorStatus.Ok); // Start linii
                    return;
                }

                // Zbieramy wyniki
                var validResults = new List<MachineCheckResult>();
                bool anyOldData = false;

                foreach (var machine in machines)
                {
                    var data = GetMachineData(machine, dmc1, dmc2);

                    if (data.IsFound)
                    {
                        // REGUŁA 1: Scrap z jakiegokolwiek stanowiska = Scrap
                        if (data.IsScrap)
                        {
                            LogEvent($"CheckInDatabaseSiemens | REMOTE SCRAP on ID {machine.MachineID}");
                            SetSiemensStatus(2, (short)TraceErrorStatus.ScrapDetected);
                            return; // Przerywamy natychmiast
                        }

                        if (data.IsOld)
                        {
                            anyOldData = true;
                        }
                        else
                        {
                            validResults.Add(data);
                        }
                    }
                }

                // --- D. Decyzja na podstawie zebranych wyników ---

                // Jeśli nie mamy żadnych ważnych wyników
                if (validResults.Count == 0)
                {
                    if (anyOldData)
                        SetSiemensStatus(2, (short)TraceErrorStatus.PreviousMachineOldData);
                    else
                        SetSiemensStatus(2, (short)TraceErrorStatus.PreviousMachineNotFound);

                    return;
                }

                // REGUŁA 2: Bierzemy dane z najświeższego stanowiska
                // Sortujemy malejąco po dacie (najnowsza pierwsza)
                validResults.Sort((a, b) => b.Date.CompareTo(a.Date));
                var freshest = validResults[0];

                LogEvent($"CheckInDatabaseSiemens | Freshest Result: {freshest.Result} from {freshest.Date}");

                // Interpretacja wyniku
                if (freshest.Result == 1 || freshest.Result == 3) // OK lub Naprawione
                {
                    SetSiemensStatus(1, (short)TraceErrorStatus.Ok);
                }
                else if (freshest.Result == 2) // NOK
                {
                    SetSiemensStatus(2, (short)TraceErrorStatus.NokDetected);
                }
                else if (freshest.Result == 0) // Brak statusu
                {
                    SetSiemensStatus(2, (short)TraceErrorStatus.StatusMissing);
                }
                else
                {
                    // Zabezpieczenie na dziwne statusy
                    SetSiemensStatus(2, (short)TraceErrorStatus.OtherError);
                }
            }
            catch (Exception e)
            {
                LogEvent($"CheckInDatabaseSiemens | ERROR: {e.Message}");
                SetSiemensStatus(2, (short)TraceErrorStatus.DatabaseCheckError);
                if (!(e is SqlException)) IsConnected = false;
            }
            LogEvent("CheckInDatabaseSiemens | END");
        }

        #endregion CHECK-IN-DATABASE-SIEMENS

        #region CHECK-IN-DATABASE-AB

        private void CheckInDatabaseAB()
        {
            LogEvent("CheckInDatabaseAB | START");
            try
            {
                // --- A. Pobranie DMC ---
                string dmc1, dmc2;
                if (NumbersOfParameters == 1)
                {
                    var res = ReadResultLongFromPLCAB();
                    dmc1 = res.DMC_Code1.ToString();
                    dmc2 = res.DMC_Code2.ToString();
                }
                else
                {
                    var res = ReadResultShortFromPLCAB();
                    dmc1 = res.DMC_Code1.ToString();
                    dmc2 = res.DMC_Code2.ToString();
                }

                if (string.IsNullOrEmpty(dmc1) && string.IsNullOrEmpty(dmc2))
                {
                    SetABStatus(2, (short)TraceErrorStatus.NoDmcCode);
                    return;
                }

                // --- B. Sprawdzenie lokalne ---
                // 1. Czy ten detal jest już ZŁOMEM w naszej bazie?
                if (_repository.IsScrapDetected(dmc1, dmc2))
                {
                    LogEvent("CheckInDatabaseAB | LOCAL SCRAP DETECTED");
                    SetABStatus(2, (short)TraceErrorStatus.ScrapDetected);
                    return;
                }

                // 2. Czy już tu był?
                if (CheckOnlyOnce && _repository.IsDmcProcessed(int.Parse(ID), dmc1, dmc2))
                {
                    SetABStatus(2, (short)TraceErrorStatus.AlreadyProcessed);
                    return;
                }

                // --- C. Sprawdzenie Poprzednich Maszyn ---
                var machines = new System.Collections.Generic.List<TemplatePreviousMachine>();
                if (PreviousMachine1.MachineID > 0) machines.Add(PreviousMachine1);
                if (PreviousMachine2.MachineID > 0) machines.Add(PreviousMachine2);
                if (PreviousMachine3.MachineID > 0) machines.Add(PreviousMachine3);

                if (machines.Count == 0)
                {
                    SetABStatus(1, (short)TraceErrorStatus.Ok); // Start linii
                    return;
                }

                // Zbieramy wyniki
                var validResults = new System.Collections.Generic.List<MachineCheckResult>();
                bool anyOldData = false;

                foreach (var machine in machines)
                {
                    var data = GetMachineData(machine, dmc1, dmc2);

                    if (data.IsFound)
                    {
                        // REGUŁA 1: Scrap z jakiegokolwiek stanowiska = Scrap
                        if (data.IsScrap)
                        {
                            LogEvent($"CheckInDatabaseAB | REMOTE SCRAP on ID {machine.MachineID}");
                            SetABStatus(2, (short)TraceErrorStatus.ScrapDetected);
                            return;// Przerywamy natychmiast
                        }

                        if (data.IsOld) anyOldData = true;
                        else validResults.Add(data);
                    }
                }

                // --- D. Decyzja na podstawie zebranych wyników ---

                // Jeśli nie mamy żadnych ważnych wyników
                if (validResults.Count == 0)
                {
                    if (anyOldData) SetABStatus(2, (short)TraceErrorStatus.PreviousMachineOldData);
                    else SetABStatus(2, (short)TraceErrorStatus.PreviousMachineNotFound);
                    return;
                }

                // REGUŁA 2: Bierzemy dane z najświeższego stanowiska
                // Sortujemy malejąco po dacie (najnowsza pierwsza)
                validResults.Sort((a, b) => b.Date.CompareTo(a.Date));
                var freshest = validResults[0];

                LogEvent($"CheckInDatabaseSiemens | Freshest Result: {freshest.Result} from {freshest.Date}");

                // Interpretacja wyniku

                if (freshest.Result == 1 || freshest.Result == 3) // OK lub Naprawione
                {
                    SetABStatus(1, (short)TraceErrorStatus.Ok);
                }
                else if (freshest.Result == 2) // NOK
                {
                    SetABStatus(2, (short)TraceErrorStatus.NokDetected);
                }
                else if (freshest.Result == 0) // Brak statusu
                {
                    SetABStatus(2, (short)TraceErrorStatus.StatusMissing);
                }
                else
                {
                    // Zabezpieczenie na dziwne statusy
                    SetABStatus(2, (short)TraceErrorStatus.OtherError);
                }
            }
            catch (Exception e)
            {
                LogEvent($"CheckInDatabaseAB | ERROR: {e.Message}");
                SetABStatus(2, (short)TraceErrorStatus.DatabaseCheckError);
                if (!(e is System.Data.SqlClient.SqlException)) IsConnected = false;
            }
            LogEvent("CheckInDatabaseAB | END");
        }

        #endregion CHECK-IN-DATABASE-AB

        private MachineCheckResult GetMachineData(TemplatePreviousMachine machine, string dmc1, string dmc2)
        {
            var output = new MachineCheckResult { IsFound = false, IsOld = false };

            try
            {
                // 1. Konfiguracja i połączenie (jak wcześniej)
                var masterRepo = new SqlTraceRepository(DBMasterServer, DBMasterPort, DBMasterDatabase, DBMasterUser, DBMasterPassword);
                var config = masterRepo.GetRemoteMachineConfig(machine.MachineID);

                string targetIp = (config != null && !string.IsNullOrEmpty(config.Ip)) ? config.Ip : DBMasterServer;
                string targetUser = (config != null && !string.IsNullOrEmpty(config.User)) ? config.User : DBMasterUser;
                string targetPass = (config != null && !string.IsNullOrEmpty(config.Password)) ? config.Password : DBMasterPassword;

                var remoteRepo = new SqlTraceRepository(targetIp, DBMasterPort, DBMasterDatabase, targetUser, targetPass);

                // 2. Pobranie danych
                var logEntry = remoteRepo.GetLatestLogEntry(machine.MachineID, dmc1, dmc2, machine.CheckSecondaryCode);

                if (logEntry.Result == null || logEntry.Timestamp == null)
                {
                    return output; // Nie znaleziono
                }

                output.IsFound = true;
                output.Result = logEntry.Result.Value;
                output.Date = logEntry.Timestamp.Value;

                // 3. Weryfikacja "świeżości"
                if (machine.MaxDaysNumber > 0)
                {
                    double daysDiff = (DateTime.Now - output.Date).TotalDays;
                    if (daysDiff > machine.MaxDaysNumber)
                    {
                        output.IsOld = true;
                    }
                }

                return output;
            }
            catch (Exception e)
            {
                LogEvent($"GetMachineData | ID: {machine.MachineID} | Error: {e.Message}");
                return output; // Traktujemy błąd połączenia jako brak danych (lub można dodać flagę IsError)
            }
        }

        private void SetSiemensStatus(short partStatus, short errorStatus)
        {
            S7_data_Write.Part_Status = partStatus;
            S7_data_Write.Task_Confirm_From_PC = 1;
            S7_data_Write.Error_Status = errorStatus;
        }

        private void SetABStatus(short partStatus, short errorStatus)
        {
            AB_data_Write.Part_Status = partStatus;
            AB_data_Write.Task_Confirm_From_PC = 1;
            AB_data_Write.Error_Status = errorStatus;
        }

        #endregion CHECK-IN-DATABASE

        #region READ-PARAMETERS-AND-SAVE-TO-DATABASE

        public void ReadParametersAndSaveToDatabase()
        {
            if (Type == "Siemens")
            {
                if (NumbersOfParameters == 1)
                {
                    ReadParametersAndSaveToDatabaseLongSiemens();
                }
                else
                {
                    ReadParametersAndSaveToDatabaseShortSiemens();
                }
            }
            else
            {
                if (NumbersOfParameters == 1)
                {
                    ReadParametersAndSaveToDatabaseLongAB();
                }
                else
                {
                    ReadParametersAndSaveToDatabaseShortAB();
                }
            }
        }

        #region READ-PARAMETERS-AND-SAVE-TO-DATABASE-SIEMENS

        private void ReadParametersAndSaveToDatabaseLongSiemens()
        {
            LogEvent("ReadParametersAndSaveToDatabaseLongSiemens | START");

            try
            {
                // 1. Pobierz dane z PLC (tak jak wcześniej)
                ReadResultLongFromPLCSiemens();

                // 2. Walidacja
                if (string.IsNullOrEmpty(S7_data_ResultLong.DMC_Code1.ToString()) &&
                    string.IsNullOrEmpty(S7_data_ResultLong.DMC_Code2.ToString()))
                {
                    throw new Exception("No DMC Code");
                }

                // 3. Mapowanie na uniwersalny model (DTO)
                var logModel = new TraceLogModel
                {
                    MachineId = int.Parse(ID), // Zakładam, że ID w klasie to string parsujący się na int
                    DmcCode1 = S7_data_ResultLong.DMC_Code1.ToString(),
                    DmcCode2 = S7_data_ResultLong.DMC_Code2.ToString(),
                    OperationResult1 = S7_data_ResultLong.Operation_Result1,
                    OperationResult2 = S7_data_ResultLong.Operation_Result2,
                    OperationDateTime1 = new DateTime(S7_data_ResultLong.Operation_DateTime1.Year, S7_data_ResultLong.Operation_DateTime1.Month, S7_data_ResultLong.Operation_DateTime1.Day, S7_data_ResultLong.Operation_DateTime1.Hour, S7_data_ResultLong.Operation_DateTime1.Minute, S7_data_ResultLong.Operation_DateTime1.Second),
                    OperationDateTime2 = DateTime.Now,
                    Reference = S7_data_ResultLong.Reference.ToString(),
                    CycleTime = S7_data_ResultLong.Cycle_Time,
                    Operator = S7_data_ResultLong.Operator.ToString(),

                    // Mapowanie tablic - to jest kluczowe uproszczenie!
                    Ints = S7_data_ResultLong.dints,
                    Reals = S7_data_ResultLong.reals
                };

                // Konwersja DTLs (bo formaty daty mogą być różne w PLC i C#)
                for (int i = 0; i < 5; i++)
                {
                    var dtl = S7_data_ResultLong.dtls[i];
                    logModel.Dtls[i] = new DateTime(dtl.Year, dtl.Month, dtl.Day, dtl.Hour, dtl.Minute, dtl.Second);
                }

                // Konwersja Stringów
                for (int i = 0; i < 5; i++)
                {
                    logModel.Strings[i] = S7_data_ResultLong.strings[i].ToString();
                }

                // 4. Zapis do bazy poprzez Repozytorium
                _repository.SaveLog(logModel);

                S7_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (Exception e)
            {
                // Obsługa błędów bez zmian
                S7_data_Write.Task_Confirm_From_PC = 2;
                S7_data_Write.Error_Status = (short)(e.Message == "No DMC Code" ? 1 : 9);
                LogEvent($"ReadParametersAndSaveToDatabaseLongSiemens | {e.Message}");
                IsConnected = false;
            }

            LogEvent("ReadParametersAndSaveToDatabaseLongSiemens | END");
        }

        private void ReadParametersAndSaveToDatabaseShortSiemens()
        {
            LogEvent("ReadParametersAndSaveToDatabaseShortSiemens | START");

            try
            {
                // 1. Pobranie danych
                ReadResultShortFromPLCSiemens();

                // 2. Walidacja
                if (string.IsNullOrEmpty(S7_data_ResultShort.DMC_Code1.ToString()) &&
                    string.IsNullOrEmpty(S7_data_ResultShort.DMC_Code2.ToString()))
                {
                    throw new Exception("No DMC Code");
                }

                // 3. Mapowanie na DTO
                var logModel = new TraceLogModel
                {
                    MachineId = int.Parse(ID),
                    DmcCode1 = S7_data_ResultShort.DMC_Code1.ToString(),
                    DmcCode2 = S7_data_ResultShort.DMC_Code2.ToString(),
                    OperationResult1 = S7_data_ResultShort.Operation_Result1,
                    OperationResult2 = S7_data_ResultShort.Operation_Result2,
                    OperationDateTime1 = new DateTime(S7_data_ResultShort.Operation_DateTime1.Year, S7_data_ResultShort.Operation_DateTime1.Month, S7_data_ResultShort.Operation_DateTime1.Day, S7_data_ResultShort.Operation_DateTime1.Hour, S7_data_ResultShort.Operation_DateTime1.Minute, S7_data_ResultShort.Operation_DateTime1.Second),
                    OperationDateTime2 = DateTime.Now,
                    Reference = S7_data_ResultShort.Reference.ToString(),
                    CycleTime = S7_data_ResultShort.Cycle_Time,
                    Operator = S7_data_ResultShort.Operator.ToString(),

                    // Bezpośrednie przypisanie tablic (rozmiary pasują do definicji w DTO lub są mniejsze)
                    Ints = S7_data_ResultShort.dints,
                    Reals = S7_data_ResultShort.reals
                };

                // Konwersja DTLs (Short ma 3 elementy)
                for (int i = 0; i < 3; i++)
                {
                    var dtl = S7_data_ResultShort.dtls[i];
                    logModel.Dtls[i] = new DateTime(dtl.Year, dtl.Month, dtl.Day, dtl.Hour, dtl.Minute, dtl.Second);
                }

                // Konwersja Stringów (Short ma 7 elementów)
                for (int i = 0; i < 7; i++)
                {
                    logModel.Strings[i] = S7_data_ResultShort.strings[i].ToString();
                }

                // 4. Zapis
                _repository.SaveLog(logModel);

                S7_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (Exception e)
            {
                S7_data_Write.Task_Confirm_From_PC = 2;
                S7_data_Write.Error_Status = (short)(e.Message == "No DMC Code" ? 1 : 9);
                LogEvent($"ReadParametersAndSaveToDatabaseShortSiemens | {e.Message}");
                IsConnected = false;
            }

            LogEvent("ReadParametersAndSaveToDatabaseShortSiemens | END");
        }

        #endregion READ-PARAMETERS-AND-SAVE-TO-DATABASE-SIEMENS

        #region READ-PARAMETERS-AND-SAVE-TO-DATABASE-AB

        private void ReadParametersAndSaveToDatabaseLongAB()
        {
            LogEvent("ReadParametersAndSaveToDatabaseLongAB | START");
            try
            {
                // 1. Pobranie danych
                ReadResultLongFromPLCAB();

                // 2. Walidacja
                if (string.IsNullOrEmpty(AB_data_ResultLong.DMC_Code1.ToString()) &&
                    string.IsNullOrEmpty(AB_data_ResultLong.DMC_Code2.ToString()))
                {
                    throw new Exception("No DMC Code");
                }

                // 3. Mapowanie na DTO
                var logModel = new TraceLogModel
                {
                    MachineId = int.Parse(ID),
                    DmcCode1 = AB_data_ResultLong.DMC_Code1.ToString(),
                    DmcCode2 = AB_data_ResultLong.DMC_Code2.ToString(),
                    OperationResult1 = AB_data_ResultLong.Operation_Result1,
                    OperationResult2 = AB_data_ResultLong.Operation_Result2,
                    OperationDateTime1 = AB_data_ResultLong.Operation_DateTime1.ToDateTime(),
                    OperationDateTime2 = DateTime.Now,
                    Reference = AB_data_ResultLong.Reference.ToString(),
                    CycleTime = AB_data_ResultLong.Cycle_Time,
                    Operator = AB_data_ResultLong.Operator.ToString(),

                    // Tablice
                    Ints = AB_data_ResultLong.dints,
                    Reals = AB_data_ResultLong.reals // Long ma 100 elementów
                };

                // Konwersja DTLs (Long ma 5 elementów)
                for (int i = 0; i < 5; i++)
                {
                    logModel.Dtls[i] = AB_data_ResultLong.dtls[i].ToDateTime();
                }

                // Konwersja Stringów (Long ma 5 elementów)
                for (int i = 0; i < 5; i++)
                {
                    logModel.Strings[i] = AB_data_ResultLong.strings[i].ToString();
                }

                // 4. Zapis
                _repository.SaveLog(logModel);

                AB_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (Exception e)
            {
                AB_data_Write.Task_Confirm_From_PC = 2;
                AB_data_Write.Error_Status = (short)(e.Message == "No DMC Code" ? 1 : 9);
                LogEvent($"ReadParametersAndSaveToDatabaseLongAB | {e.Message}");
                IsConnected = false;
            }

            LogEvent("ReadParametersAndSaveToDatabaseLongAB | END");
        }

        private void ReadParametersAndSaveToDatabaseShortAB()
        {
            LogEvent("ReadParametersAndSaveToDatabaseShortAB | START");
            try
            {
                // 1. Pobranie danych
                ReadResultShortFromPLCAB();

                // 2. Walidacja
                if (string.IsNullOrEmpty(AB_data_ResultShort.DMC_Code1.ToString()) &&
                    string.IsNullOrEmpty(AB_data_ResultShort.DMC_Code2.ToString()))
                {
                    throw new Exception("No DMC Code");
                }

                // 3. Mapowanie na DTO
                var logModel = new TraceLogModel
                {
                    MachineId = int.Parse(ID),
                    DmcCode1 = AB_data_ResultShort.DMC_Code1.ToString(),
                    DmcCode2 = AB_data_ResultShort.DMC_Code2.ToString(),
                    OperationResult1 = AB_data_ResultShort.Operation_Result1,
                    OperationResult2 = AB_data_ResultShort.Operation_Result2,
                    OperationDateTime1 = AB_data_ResultShort.Operation_DateTime1.ToDateTime(),
                    OperationDateTime2 = DateTime.Now,
                    Reference = AB_data_ResultShort.Reference.ToString(),
                    CycleTime = AB_data_ResultShort.Cycle_Time,
                    Operator = AB_data_ResultShort.Operator.ToString(),

                    // Tablice
                    Ints = AB_data_ResultShort.dints,
                    Reals = AB_data_ResultShort.reals // Short ma 10 elementów
                };

                // Konwersja DTLs (Short ma 3 elementy)
                for (int i = 0; i < 3; i++)
                {
                    logModel.Dtls[i] = AB_data_ResultShort.dtls[i].ToDateTime();
                }

                // Konwersja Stringów (Short ma 7 elementów)
                for (int i = 0; i < 7; i++)
                {
                    logModel.Strings[i] = AB_data_ResultShort.strings[i].ToString();
                }

                // 4. Zapis
                _repository.SaveLog(logModel);

                AB_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (Exception e)
            {
                AB_data_Write.Task_Confirm_From_PC = 2;
                AB_data_Write.Error_Status = (short)(e.Message == "No DMC Code" ? 1 : 9);
                LogEvent($"ReadParametersAndSaveToDatabaseShortAB | {e.Message}");
                IsConnected = false;
            }

            LogEvent("ReadParametersAndSaveToDatabaseShortAB | END");
        }

        #endregion READ-PARAMETERS-AND-SAVE-TO-DATABASE-AB

        #endregion READ-PARAMETERS-AND-SAVE-TO-DATABASE
    }
}
