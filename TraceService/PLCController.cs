using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Data;
using System.Data.SqlClient;
using AutomatedSolutions.ASCommStd;
using SIS7 = AutomatedSolutions.ASCommStd.SI.S7;
using ABLogix = AutomatedSolutions.ASCommStd.AB.Logix;
using System.Net.Sockets;

namespace TraceService
{
    public class PLCController
    {
        private static readonly Object lockObj = new Object();
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
            lock (lockObj)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(@"C:\Trace");
                    using (StreamWriter writer = new StreamWriter(@"C:\Trace\" + DateTime.Now.ToString("yyyyMMdd") + "_event_" + ID.ToString() + ".txt", true))
                    {
                        writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " | " + message);
                    }
                }
                catch (Exception)
                {
                }
            }
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

        public void CheckInDatabaseTEST()
        {
            LogEvent("CheckInDatabaseTEST | START");

            try
            {
                String DMCCode1 = "000002SW09700125168144921";
                String DMCCode2 = "";

                if (DMCCode1 == "" && DMCCode2 == "")
                {
                    throw new Exception("No DMC Code");
                }

                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE ( (dmc_code1 = @p_DMCCode_1 AND LEN(dmc_code1) > 0) OR (dmc_code2 = @p_DMCCode_2 AND LEN(dmc_code2) > 0) ) AND (operation_result1 = 4 OR operation_result2 = 4)";
                SQLCommand.Parameters.Add("@p_DMCCode_1", SqlDbType.VarChar, 256).Value = DMCCode1;
                SQLCommand.Parameters.Add("@p_DMCCode_2", SqlDbType.VarChar, 256).Value = DMCCode2;

                SqlDataReader SQLreader = SQLCommand.ExecuteReader();

                Boolean found4 = SQLreader.Read();
                SQLreader.Close();
                SQLConnection.Close();

                if (found4)
                {
                    LogEvent("CheckInDatabaseTEST | FOUND OPERATION 4");
                    LogEvent("CheckInDatabaseTEST | Part_Status 2");
                    LogEvent("CheckInDatabaseTEST | Task_Confirm_From_PC 1");
                    return;
                }

                if (CheckOnlyOnce)
                {
                    SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                    SQLConnection.Open();
                    SQLCommand = SQLConnection.CreateCommand();

                    SQLCommand.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE machine_id = @p_MachineID AND (dmc_code1 = @p_DMCCode_1 OR dmc_code2 = @p_DMCCode_2) ORDER BY id DESC";
                    SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = ID;
                    SQLCommand.Parameters.Add("@p_DMCCode_1", SqlDbType.VarChar, 256).Value = DMCCode1;
                    SQLCommand.Parameters.Add("@p_DMCCode_2", SqlDbType.VarChar, 256).Value = DMCCode2;

                    SQLreader = SQLCommand.ExecuteReader();

                    Boolean foundOnce = SQLreader.Read();
                    SQLreader.Close();
                    SQLConnection.Close();

                    if (foundOnce)
                    {
                        LogEvent("CheckInDatabaseTEST | FOUND ONCE");
                        LogEvent("CheckInDatabaseTEST | Part_Status 2");
                        LogEvent("CheckInDatabaseTEST | Task_Confirm_From_PC 1");
                        return;
                    }
                }

                if (PreviousMachine1.MachineID == 0 && PreviousMachine2.MachineID == 0 && PreviousMachine3.MachineID == 0)
                {
                    LogEvent("CheckInDatabaseTEST | NO PREVIOUS MACHINE");
                    LogEvent("CheckInDatabaseTEST | Part_Status 1");
                    LogEvent("CheckInDatabaseTEST | Task_Confirm_From_PC 1");
                    return;
                }

                Int32 foundOperationResult = 0;
                DateTime foundOperationDatetime = DateTime.MinValue;
                if (PreviousMachine1.MachineID > 0)
                {
                    var result = CheckInDatabasePreviousMachine(PreviousMachine1, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult;
                    foundOperationDatetime = result.operationDatetime;
                }
                if (PreviousMachine2.MachineID > 0)
                {
                    var result = CheckInDatabasePreviousMachine(PreviousMachine2, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    if (result.operationDatetime > foundOperationDatetime)
                    {
                        foundOperationResult = result.operationResult == 2 || result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    }
                    foundOperationDatetime = result.operationDatetime;
                }
                if (PreviousMachine3.MachineID > 0)
                {
                    var result = CheckInDatabasePreviousMachine(PreviousMachine3, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    if (result.operationDatetime > foundOperationDatetime)
                    {
                        foundOperationResult = result.operationResult == 2 || result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    }
                    foundOperationDatetime = result.operationDatetime;
                }

                LogEvent($"CheckInDatabaseTEST | FOUND OPERATION {foundOperationResult}");
                if (foundOperationResult == 1 || foundOperationResult == 3)
                {
                    LogEvent("CheckInDatabaseTEST | Part_Status 1");
                    LogEvent("CheckInDatabaseTEST | Task_Confirm_From_PC 1");
                }
                else
                {
                    LogEvent("CheckInDatabaseTEST | Part_Status 2");
                    LogEvent("CheckInDatabaseTEST | Task_Confirm_From_PC 1");
                }
            }
            catch (SqlException e)
            {
                LogEvent($"CheckInDatabaseTEST | SQL | {e.Message}");
            }
            catch (Exception e)
            {
                LogEvent($"CheckInDatabaseTEST | {e.Message}");
                IsConnected = false;
            }

            LogEvent("CheckInDatabaseTEST | END");

        }

        #region CHECK-IN-DATABASE-SIEMENS

        private void CheckInDatabaseSiemens()
        {
            LogEvent("CheckInDatabaseSiemens | START");

            try {
                String DMCCode1 = "";
                String DMCCode2 = "";
                if (NumbersOfParameters == 1)
                {
                    SiemensDataResultLong result = ReadResultLongFromPLCSiemens();
                    DMCCode1 = result.DMC_Code1.ToString();
                    DMCCode2 = result.DMC_Code2.ToString();
                }
                else
                {
                    SiemensDataResultShort result = ReadResultShortFromPLCSiemens();
                    DMCCode1 = result.DMC_Code1.ToString();
                    DMCCode2 = result.DMC_Code2.ToString();
                }

                if (DMCCode1 == "" && DMCCode2 == "")
                {
                    S7_data_Write.Part_Status = 2;
                    S7_data_Write.Task_Confirm_From_PC = 1;
                    S7_data_Write.Error_Status = 1;
                    throw new Exception("No DMC Code");
                }

                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE ( (dmc_code1 = @p_DMCCode_1 AND LEN(dmc_code1) > 0) OR (dmc_code2 = @p_DMCCode_2 AND LEN(dmc_code2) > 0) ) AND (operation_result1 = 4 OR operation_result2 = 4)";
                SQLCommand.Parameters.Add("@p_DMCCode_1", SqlDbType.VarChar, 256).Value = DMCCode1;
                SQLCommand.Parameters.Add("@p_DMCCode_2", SqlDbType.VarChar, 256).Value = DMCCode2;

                SqlDataReader SQLreader = SQLCommand.ExecuteReader();

                Boolean found4 = SQLreader.Read();
                SQLreader.Close();
                SQLConnection.Close();

                if (found4)
                {
                    LogEvent("CheckInDatabaseSiemens | FOUND OPERATION 4");
                    S7_data_Write.Part_Status = 2;
                    S7_data_Write.Task_Confirm_From_PC = 1;
                    S7_data_Write.Error_Status = 50;
                    return;
                }

                if (CheckOnlyOnce)
                {
                    SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                    SQLConnection.Open();
                    SQLCommand = SQLConnection.CreateCommand();

                    SQLCommand.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE machine_id = @p_MachineID AND (dmc_code1 = @p_DMCCode_1 OR dmc_code2 = @p_DMCCode_2) ORDER BY id DESC";
                    SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = ID;
                    SQLCommand.Parameters.Add("@p_DMCCode_1", SqlDbType.VarChar, 256).Value = DMCCode1;
                    SQLCommand.Parameters.Add("@p_DMCCode_2", SqlDbType.VarChar, 256).Value = DMCCode2;

                    SQLreader = SQLCommand.ExecuteReader();

                    Boolean foundOnce = SQLreader.Read();
                    SQLreader.Close();
                    SQLConnection.Close();

                    if (foundOnce)
                    {
                        S7_data_Write.Part_Status = 2;
                        S7_data_Write.Task_Confirm_From_PC = 1;
                        return;
                    }
                }

                if (PreviousMachine1.MachineID == 0 && PreviousMachine2.MachineID == 0 && PreviousMachine3.MachineID == 0)
                {
                    LogEvent("CheckInDatabaseSiemens | NO PREVIOUS MACHINE");
                    S7_data_Write.Part_Status = 1;
                    S7_data_Write.Task_Confirm_From_PC = 1;
                    return;
                }

                Int32 foundOperationResult = 0;
                DateTime foundOperationDatetime = DateTime.MinValue;
                if (PreviousMachine1.MachineID > 0)
                {
                    var result = CheckInDatabasePreviousMachine(PreviousMachine1, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult;
                    foundOperationDatetime = result.operationDatetime;
                }
                if (PreviousMachine2.MachineID > 0)
                {
                    var result = CheckInDatabasePreviousMachine(PreviousMachine2, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    if (result.operationDatetime > foundOperationDatetime)
                    {
                        foundOperationResult = result.operationResult == 2 || result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    }
                    foundOperationDatetime = result.operationDatetime;
                }
                if (PreviousMachine3.MachineID > 0)
                {
                    var result = CheckInDatabasePreviousMachine(PreviousMachine3, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    if (result.operationDatetime > foundOperationDatetime)
                    {
                        foundOperationResult = result.operationResult == 2 || result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    }
                    foundOperationDatetime = result.operationDatetime;
                }

                //LogEvent($"CheckInDatabaseSiemens | FOUND OPERATION {foundOperationResult}");
                if (foundOperationResult == 1 || foundOperationResult == 3)
                {
                    S7_data_Write.Part_Status = 1;
                    S7_data_Write.Task_Confirm_From_PC = 1;
                    S7_data_Write.Error_Status = 0;
                }
                else
                {
                    S7_data_Write.Part_Status = 2;
                    S7_data_Write.Task_Confirm_From_PC = 1;
                    S7_data_Write.Error_Status = (short)(foundOperationResult == 4 ? 50 : foundOperationResult == -1 ? 53 : foundOperationResult == -2 ? 52 : 51);
                }
            }
            catch (SqlException e)
            {
                LogEvent($"CheckInDatabaseSiemens | SQL | {e.Message}");
                S7_data_Write.Part_Status = 2;
                S7_data_Write.Task_Confirm_From_PC = 1;
                S7_data_Write.Error_Status = 3;
            }
            catch (Exception e)
            {
                LogEvent($"CheckInDatabaseSiemens | {e.Message}");
                IsConnected = false;
            }

            LogEvent("CheckInDatabaseSiemens | END");
        }

        #endregion CHECK-IN-DATABASE-SIEMENS

        #region CHECK-IN-DATABASE-AB

        private void CheckInDatabaseAB()
        {
            LogEvent("CheckInDatabaseAB | START");

            try {
                String DMCCode1 = "";
                String DMCCode2 = "";
                if (NumbersOfParameters == 1)
                {
                    //LogEvent("CheckInDatabaseAB | Numbers Of Parameters 1");
                    ABDataResultLong result = ReadResultLongFromPLCAB();
                    DMCCode1 = result.DMC_Code1.ToString();
                    DMCCode2 = result.DMC_Code2.ToString();
                }
                else
                {
                    //LogEvent("CheckInDatabaseAB | Numbers Of Parameters 2");
                    ABDataResultShort result = ReadResultShortFromPLCAB();
                    DMCCode1 = result.DMC_Code1.ToString();
                    DMCCode2 = result.DMC_Code2.ToString();
                }

                if (DMCCode1 == "" && DMCCode2 == "")
                {
                    AB_data_Write.Part_Status = 2;
                    AB_data_Write.Task_Confirm_From_PC = 1;
                    AB_data_Write.Error_Status = 1;
                    throw new Exception("No DMC Code");
                }

                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE ( (dmc_code1 = @p_DMCCode_1 AND LEN(dmc_code1) > 0) OR (dmc_code2 = @p_DMCCode_2 AND LEN(dmc_code2) > 0) ) AND (operation_result1 = 4 OR operation_result2 = 4)";
                SQLCommand.Parameters.Add("@p_DMCCode_1", SqlDbType.VarChar, 256).Value = DMCCode1;
                SQLCommand.Parameters.Add("@p_DMCCode_2", SqlDbType.VarChar, 256).Value = DMCCode2;

                SqlDataReader SQLreader = SQLCommand.ExecuteReader();

                Boolean found4 = SQLreader.Read();
                SQLreader.Close();
                SQLConnection.Close();

                if (found4)
                {
                    LogEvent("CheckInDatabaseAB | FOUND OPERATION 4");
                    AB_data_Write.Part_Status = 2;
                    AB_data_Write.Task_Confirm_From_PC = 1;
                    AB_data_Write.Error_Status = 50;
                    return;
                }

                if (CheckOnlyOnce)
                {
                    //LogEvent("CheckInDatabaseAB | CheckOnlyOnce START");
                    SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                    SQLConnection.Open();
                    SQLCommand = SQLConnection.CreateCommand();

                    SQLCommand.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE machine_id = @p_MachineID AND (dmc_code1 = @p_DMCCode_1 OR dmc_code2 = @p_DMCCode_2) ORDER BY id DESC";
                    SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = ID;
                    SQLCommand.Parameters.Add("@p_DMCCode_1", SqlDbType.VarChar, 256).Value = DMCCode1;
                    SQLCommand.Parameters.Add("@p_DMCCode_2", SqlDbType.VarChar, 256).Value = DMCCode2;

                    SQLreader = SQLCommand.ExecuteReader();

                    Boolean foundOnce = SQLreader.Read();
                    SQLreader.Close();
                    SQLConnection.Close();

                    if (foundOnce)
                    {
                        //LogEvent("CheckInDatabaseAB | CheckOnlyOnce Found Once");
                        AB_data_Write.Part_Status = 2;
                        AB_data_Write.Task_Confirm_From_PC = 1;
                        return;
                    }
                }

                if (PreviousMachine1.MachineID == 0 && PreviousMachine2.MachineID == 0 && PreviousMachine3.MachineID == 0)
                {
                   // LogEvent("CheckInDatabaseAB | NO PREVIOUS MACHINE");
                    AB_data_Write.Part_Status = 1;
                    AB_data_Write.Task_Confirm_From_PC = 1;
                    return;
                }

                Int32 foundOperationResult = 0;
                DateTime foundOperationDatetime = DateTime.MinValue;
                if (PreviousMachine1.MachineID > 0)
                {
                    //LogEvent("CheckInDatabaseAB | MachineID 1 > 0");
                    var result = CheckInDatabasePreviousMachine(PreviousMachine1, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult;
                    foundOperationDatetime = result.operationDatetime;
                }
                if (PreviousMachine2.MachineID > 0)
                {
                    //LogEvent("CheckInDatabaseAB | MachineID 2 > 0");
                    var result = CheckInDatabasePreviousMachine(PreviousMachine2, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    if (result.operationDatetime > foundOperationDatetime)
                    {
                        foundOperationResult = result.operationResult == 2 || result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    }
                    foundOperationDatetime = result.operationDatetime;
                }
                if (PreviousMachine3.MachineID > 0)
                {
                    //LogEvent("CheckInDatabaseAB | MachineID 3 > 0");
                    var result = CheckInDatabasePreviousMachine(PreviousMachine3, DMCCode1, DMCCode2);
                    foundOperationResult = result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    if (result.operationDatetime > foundOperationDatetime)
                    {
                        foundOperationResult = result.operationResult == 2 || result.operationResult == 4 ? result.operationResult : foundOperationResult;
                    }
                    foundOperationDatetime = result.operationDatetime;
                }

                LogEvent($"CheckInDatabaseAB | FOUND OPERATION {foundOperationResult}");
                if (foundOperationResult == 1 || foundOperationResult == 3)
                {
                    //LogEvent("CheckInDatabaseAB | FOUND OPERATION 1 OR 3");
                    AB_data_Write.Part_Status = 1;
                    AB_data_Write.Task_Confirm_From_PC = 1;
                    AB_data_Write.Error_Status = 0;
                }
                else
                {
                    //LogEvent("CheckInDatabaseAB | FOUND OPERATION ELSE");
                    AB_data_Write.Part_Status = 2;
                    AB_data_Write.Task_Confirm_From_PC = 1;
                    AB_data_Write.Error_Status = (short)(foundOperationResult == 4 ? 50 : foundOperationResult == -1 ? 53 : foundOperationResult == -2 ? 52 : 51);
                }
            }
            catch (SqlException e)
            {
                AB_data_Write.Part_Status = 2;
                AB_data_Write.Task_Confirm_From_PC = 1;
                AB_data_Write.Error_Status = 3;
                LogEvent($"CheckInDatabaseAB | SQL | {e.Message}");
            }
            catch (Exception e)
            {
                LogEvent($"CheckInDatabaseAB | {e.Message}");
                IsConnected = false;
            }

            LogEvent("CheckInDatabaseAB | END");
        }

        #endregion CHECK-IN-DATABASE-AB

        private (Int32 operationResult, DateTime operationDatetime) CheckInDatabasePreviousMachine(TemplatePreviousMachine previousMachine, String DMCCode1, String DMCCode2)
        {
            Int32 foundOperationResult = 0;
            DateTime foundOperationDatetime = DateTime.MinValue;
            Boolean found4 = false;
            Boolean checkIfFound = false;
            try {
                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBMasterServer + "," + DBMasterPort + ";Initial Catalog=" + DBMasterDatabase + ";User ID=" + DBMasterUser + ";Password=" + DBMasterPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "SELECT machines.numbers_of_parameters, lines.database_ip, lines.database_login, lines.database_password FROM " +
                                            "(SELECT id_line, numbers_of_parameters FROM dbo.machines WHERE machine_id = @p_MachineID) machines " +
                                            "LEFT JOIN dbo.lines lines ON lines.id = machines.id_line";
                SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = previousMachine.MachineID;

                SqlDataReader SQLreader = SQLCommand.ExecuteReader();
                SQLreader.Read();

                byte PreviosMachineNumbersOfParameters = SQLreader.IsDBNull(0) ? (Byte)1 : SQLreader.GetByte(0);
                String PreviosMachineServer = SQLreader.IsDBNull(1) ? DBMasterServer : SQLreader.GetString(1).Trim();
                String PreviosMachineUser = SQLreader.IsDBNull(2) ? DBMasterUser : SQLreader.GetString(2).Trim();
                String PreviosMachinePassword = SQLreader.IsDBNull(3) ? DBMasterPassword : SQLreader.GetString(3).Trim();

                SQLreader.Close();
                SQLConnection.Close();

                //----------------------------------------------------------------------------------------------------

                // Sprawdzanie czy operation_result == 4
                SQLConnection = new SqlConnection(@"Data source=" + PreviosMachineServer + "," + DBMasterPort + ";Initial Catalog=" + DBMasterDatabase + ";User ID=" + PreviosMachineUser + ";Password=" + PreviosMachinePassword + ";");
                SQLConnection.Open();
                SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE ( (dmc_code1 = @p_DMCCode_1 AND LEN(dmc_code1) > 0) OR (dmc_code2 = @p_DMCCode_2 AND LEN(dmc_code2) > 0) ) AND (operation_result1 = 4 OR operation_result2 = 4)";
                SQLCommand.Parameters.Add("@p_DMCCode_1", SqlDbType.VarChar, 256).Value = DMCCode1;
                SQLCommand.Parameters.Add("@p_DMCCode_2", SqlDbType.VarChar, 256).Value = DMCCode2;

                SQLreader = SQLCommand.ExecuteReader();

                found4 = SQLreader.Read();
                SQLreader.Close();
                SQLConnection.Close();

                if (found4)
                {
                    return (4, foundOperationDatetime);
                }

                // Sprawdzanie reszty
                SQLConnection = new SqlConnection(@"Data source=" + PreviosMachineServer + "," + DBMasterPort + ";Initial Catalog=" + DBMasterDatabase + ";User ID=" + PreviosMachineUser + ";Password=" + PreviosMachinePassword + ";");
                SQLConnection.Open();
                SQLCommand = SQLConnection.CreateCommand();

                String maxDays = "";
                if (previousMachine.MaxDaysNumber > 0)
                {
                    maxDays = " AND operation_datetime2 >= DATEADD(DAY, -" + previousMachine.MaxDaysNumber.ToString() + ", GETDATE())";
                }

                if (previousMachine.CheckSecondaryCode)
                {
                    SQLCommand.CommandText = "SELECT TOP(1) operation_result1, operation_result2, operation_datetime1 FROM dbo.logs WHERE machine_id = @p_MachineID AND (dmc_code1 = @p_DMCCode1 OR dmc_code2 = @p_DMCCode2) " + maxDays + " ORDER BY id DESC";
                    SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = previousMachine.MachineID;
                    SQLCommand.Parameters.Add("@p_DMCCode1", SqlDbType.VarChar, 256).Value = DMCCode1;
                    SQLCommand.Parameters.Add("@p_DMCCode2", SqlDbType.VarChar, 256).Value = DMCCode2;
                } 
                else
                {
                    SQLCommand.CommandText = "SELECT TOP(1) operation_result1, operation_result2, operation_datetime1 FROM dbo.logs WHERE machine_id = @p_MachineID AND dmc_code1 = @p_DMCCode " + maxDays + " ORDER BY id DESC";
                    SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = previousMachine.MachineID;
                    SQLCommand.Parameters.Add("@p_DMCCode", SqlDbType.VarChar, 256).Value = DMCCode1;
                }

                SQLreader = SQLCommand.ExecuteReader();
                checkIfFound = SQLreader.Read();

                SQLreader.Close();
                SQLConnection.Close();

                if (checkIfFound)
                {
                    Int32 foundOperationResult1 = SQLreader.IsDBNull(0) ? 0 : SQLreader.GetInt32(0);
                    Int32 foundOperationResult2 = SQLreader.IsDBNull(1) ? 0 : SQLreader.GetInt32(1);
                    foundOperationDatetime = SQLreader.IsDBNull(2) ? DateTime.MinValue : SQLreader.GetDateTime(2);

                    if (foundOperationResult1 == 2 || foundOperationResult2 == 2)
                    {
                        LogEvent("CheckInDatabasePreviousMachine | foundOperationResult = 2");
                        foundOperationResult = 2;
                    }
                    else if (foundOperationResult2 > foundOperationResult1)
                    {
                        LogEvent("CheckInDatabasePreviousMachine | foundOperationResult2 > foundOperationResult1");
                        foundOperationResult = foundOperationResult2;
                    }
                    else
                    {
                        LogEvent("CheckInDatabasePreviousMachine | foundOperationResult ELSE");
                        foundOperationResult = foundOperationResult1;
                    }
                } 
                else 
                {
                    SQLConnection = new SqlConnection(@"Data source=" + PreviosMachineServer + "," + DBMasterPort + ";Initial Catalog=" + DBMasterDatabase + ";User ID=" + PreviosMachineUser + ";Password=" + PreviosMachinePassword + ";");
                    SQLConnection.Open();
                    SQLCommand = SQLConnection.CreateCommand();

                    if (previousMachine.CheckSecondaryCode)
                    {
                        SQLCommand.CommandText = "SELECT TOP(1) operation_result1, operation_result2, operation_datetime1 FROM dbo.logs WHERE machine_id = @p_MachineID AND (dmc_code1 = @p_DMCCode1 OR dmc_code2 = @p_DMCCode2) ORDER BY id DESC";
                        SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = previousMachine.MachineID;
                        SQLCommand.Parameters.Add("@p_DMCCode1", SqlDbType.VarChar, 256).Value = DMCCode1;
                        SQLCommand.Parameters.Add("@p_DMCCode2", SqlDbType.VarChar, 256).Value = DMCCode2;
                    }
                    else
                    {
                        SQLCommand.CommandText = "SELECT TOP(1) operation_result1, operation_result2, operation_datetime1 FROM dbo.logs WHERE machine_id = @p_MachineID AND dmc_code1 = @p_DMCCode ORDER BY id DESC";
                        SQLCommand.Parameters.Add("@p_MachineID", SqlDbType.Int).Value = previousMachine.MachineID;
                        SQLCommand.Parameters.Add("@p_DMCCode", SqlDbType.VarChar, 256).Value = DMCCode1;
                    }

                    SQLreader = SQLCommand.ExecuteReader();
                    checkIfFound = SQLreader.Read();

                    SQLreader.Close();
                    SQLConnection.Close();

                    if (checkIfFound)
                    {
                        LogEvent("CheckInDatabasePreviousMachine | Not found at date range in database");
                        foundOperationResult = -1;
                    }
                    else
                    {
                        LogEvent("CheckInDatabasePreviousMachine | Not found in database");
                        foundOperationResult = -2;
                    }
                }
            }
            catch (SqlException e)
            {
                LogEvent($"CheckInDatabasePreviousMachine | SQL | {e.Message}");
                foundOperationResult = 0;
            }
            catch (Exception e)
            {
                LogEvent($"CheckInDatabasePreviousMachine | {e.Message}");
                IsConnected = false;
                foundOperationResult = 0;
            }

            return (foundOperationResult, foundOperationDatetime);
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
                ReadResultLongFromPLCSiemens();

                if (S7_data_ResultLong.DMC_Code1.ToString() == "" && S7_data_ResultLong.DMC_Code2.ToString() == "")
                {
                    throw new Exception("No DMC Code");
                }

                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "INSERT INTO dbo.logs " +
                                            "(machine_id, dmc_code1, dmc_code2, operation_result1, operation_result2, operation_datetime1, operation_datetime2, reference, cycle_time, operator, int_1, int_2, int_3, int_4, int_5, int_6, int_7, int_8, int_9, int_10, real_1, real_2, real_3, real_4, real_5, real_6, real_7, real_8, real_9, real_10, real_11, real_12, real_13, real_14, real_15, real_16, real_17, real_18, real_19, real_20, real_21, real_22, real_23, real_24, real_25, real_26, real_27, real_28, real_29, real_30, real_31, real_32, real_33, real_34, real_35, real_36, real_37, real_38, real_39, real_40, real_41, real_42, real_43, real_44, real_45, real_46, real_47, real_48, real_49, real_50, real_51, real_52, real_53, real_54, real_55, real_56, real_57, real_58, real_59, real_60, real_61, real_62, real_63, real_64, real_65, real_66, real_67, real_68, real_69, real_70, real_71, real_72, real_73, real_74, real_75, real_76, real_77, real_78, real_79, real_80, real_81, real_82, real_83, real_84, real_85, real_86, real_87, real_88, real_89, real_90, real_91, real_92, real_93, real_94, real_95, real_96, real_97, real_98, real_99, real_100, dtl_1, dtl_2, dtl_3, dtl_4, dtl_5, string_1, string_2, string_3, string_4, string_5) " +
                                            "VALUES " +
                                            "(@machineID, @dmcCode1, @dmcCode2, @operationResult1, @operationResult2, @operationDatetime1, @operationDatetime2, @reference, @cycleTime, @operator, @int1, @int2, @int3, @int4, @int5, @int6, @int7, @int8, @int9, @int10, @real1, @real2, @real3, @real4, @real5, @real6, @real7, @real8, @real9, @real10, @real11, @real12, @real13, @real14, @real15, @real16, @real17, @real18, @real19, @real20, @real21, @real22, @real23, @real24, @real25, @real26, @real27, @real28, @real29, @real30, @real31, @real32, @real33, @real34, @real35, @real36, @real37, @real38, @real39, @real40, @real41, @real42, @real43, @real44, @real45, @real46, @real47, @real48, @real49, @real50, @real51, @real52, @real53, @real54, @real55, @real56, @real57, @real58, @real59, @real60, @real61, @real62, @real63, @real64, @real65, @real66, @real67, @real68, @real69, @real70, @real71, @real72, @real73, @real74, @real75, @real76, @real77, @real78, @real79, @real80, @real81, @real82, @real83, @real84, @real85, @real86, @real87, @real88, @real89, @real90, @real91, @real92, @real93, @real94, @real95, @real96, @real97, @real98, @real99, @real100, @dtl1, @dtl2, @dtl3, @dtl4, @dtl5, @string1, @string2, @string3, @string4, @string5)";

                SQLCommand.Parameters.Add("@machineID", SqlDbType.Int).Value = ID;
                SQLCommand.Parameters.Add("@dmcCode1", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.DMC_Code1.ToString();
                SQLCommand.Parameters.Add("@dmcCode2", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.DMC_Code2.ToString();
                SQLCommand.Parameters.Add("@operationResult1", SqlDbType.Int).Value = S7_data_ResultLong.Operation_Result1;
                SQLCommand.Parameters.Add("@operationResult2", SqlDbType.Int).Value = S7_data_ResultLong.Operation_Result2;
                SQLCommand.Parameters.Add("@operationDatetime1", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultLong.Operation_DateTime1.Year, S7_data_ResultLong.Operation_DateTime1.Month, S7_data_ResultLong.Operation_DateTime1.Day, S7_data_ResultLong.Operation_DateTime1.Hour, S7_data_ResultLong.Operation_DateTime1.Minute, S7_data_ResultLong.Operation_DateTime1.Second);
                //SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultLong.Operation_DateTime2.Year, S7_data_ResultLong.Operation_DateTime2.Month, S7_data_ResultLong.Operation_DateTime2.Day, S7_data_ResultLong.Operation_DateTime2.Hour, S7_data_ResultLong.Operation_DateTime2.Minute, S7_data_ResultLong.Operation_DateTime2.Second);
                SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = DateTime.Now;
                SQLCommand.Parameters.Add("@reference", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.Reference.ToString();
                SQLCommand.Parameters.Add("@cycleTime", SqlDbType.Int).Value = S7_data_ResultLong.Cycle_Time;
                SQLCommand.Parameters.Add("@operator", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.Operator.ToString();
                SQLCommand.Parameters.Add("@int1", SqlDbType.Int).Value = S7_data_ResultLong.dints[0];
                SQLCommand.Parameters.Add("@int2", SqlDbType.Int).Value = S7_data_ResultLong.dints[1];
                SQLCommand.Parameters.Add("@int3", SqlDbType.Int).Value = S7_data_ResultLong.dints[2];
                SQLCommand.Parameters.Add("@int4", SqlDbType.Int).Value = S7_data_ResultLong.dints[3];
                SQLCommand.Parameters.Add("@int5", SqlDbType.Int).Value = S7_data_ResultLong.dints[4];
                SQLCommand.Parameters.Add("@int6", SqlDbType.Int).Value = S7_data_ResultLong.dints[5];
                SQLCommand.Parameters.Add("@int7", SqlDbType.Int).Value = S7_data_ResultLong.dints[6];
                SQLCommand.Parameters.Add("@int8", SqlDbType.Int).Value = S7_data_ResultLong.dints[7];
                SQLCommand.Parameters.Add("@int9", SqlDbType.Int).Value = S7_data_ResultLong.dints[8];
                SQLCommand.Parameters.Add("@int10", SqlDbType.Int).Value = S7_data_ResultLong.dints[9];
                SQLCommand.Parameters.Add("@real1", SqlDbType.Real).Value = S7_data_ResultLong.reals[0];
                SQLCommand.Parameters.Add("@real2", SqlDbType.Real).Value = S7_data_ResultLong.reals[1];
                SQLCommand.Parameters.Add("@real3", SqlDbType.Real).Value = S7_data_ResultLong.reals[2];
                SQLCommand.Parameters.Add("@real4", SqlDbType.Real).Value = S7_data_ResultLong.reals[3];
                SQLCommand.Parameters.Add("@real5", SqlDbType.Real).Value = S7_data_ResultLong.reals[4];
                SQLCommand.Parameters.Add("@real6", SqlDbType.Real).Value = S7_data_ResultLong.reals[5];
                SQLCommand.Parameters.Add("@real7", SqlDbType.Real).Value = S7_data_ResultLong.reals[6];
                SQLCommand.Parameters.Add("@real8", SqlDbType.Real).Value = S7_data_ResultLong.reals[7];
                SQLCommand.Parameters.Add("@real9", SqlDbType.Real).Value = S7_data_ResultLong.reals[8];
                SQLCommand.Parameters.Add("@real10", SqlDbType.Real).Value = S7_data_ResultLong.reals[9];
                SQLCommand.Parameters.Add("@real11", SqlDbType.Real).Value = S7_data_ResultLong.reals[10];
                SQLCommand.Parameters.Add("@real12", SqlDbType.Real).Value = S7_data_ResultLong.reals[11];
                SQLCommand.Parameters.Add("@real13", SqlDbType.Real).Value = S7_data_ResultLong.reals[12];
                SQLCommand.Parameters.Add("@real14", SqlDbType.Real).Value = S7_data_ResultLong.reals[13];
                SQLCommand.Parameters.Add("@real15", SqlDbType.Real).Value = S7_data_ResultLong.reals[14];
                SQLCommand.Parameters.Add("@real16", SqlDbType.Real).Value = S7_data_ResultLong.reals[15];
                SQLCommand.Parameters.Add("@real17", SqlDbType.Real).Value = S7_data_ResultLong.reals[16];
                SQLCommand.Parameters.Add("@real18", SqlDbType.Real).Value = S7_data_ResultLong.reals[17];
                SQLCommand.Parameters.Add("@real19", SqlDbType.Real).Value = S7_data_ResultLong.reals[18];
                SQLCommand.Parameters.Add("@real20", SqlDbType.Real).Value = S7_data_ResultLong.reals[19];
                SQLCommand.Parameters.Add("@real21", SqlDbType.Real).Value = S7_data_ResultLong.reals[20];
                SQLCommand.Parameters.Add("@real22", SqlDbType.Real).Value = S7_data_ResultLong.reals[21];
                SQLCommand.Parameters.Add("@real23", SqlDbType.Real).Value = S7_data_ResultLong.reals[22];
                SQLCommand.Parameters.Add("@real24", SqlDbType.Real).Value = S7_data_ResultLong.reals[23];
                SQLCommand.Parameters.Add("@real25", SqlDbType.Real).Value = S7_data_ResultLong.reals[24];
                SQLCommand.Parameters.Add("@real26", SqlDbType.Real).Value = S7_data_ResultLong.reals[25];
                SQLCommand.Parameters.Add("@real27", SqlDbType.Real).Value = S7_data_ResultLong.reals[26];
                SQLCommand.Parameters.Add("@real28", SqlDbType.Real).Value = S7_data_ResultLong.reals[27];
                SQLCommand.Parameters.Add("@real29", SqlDbType.Real).Value = S7_data_ResultLong.reals[28];
                SQLCommand.Parameters.Add("@real30", SqlDbType.Real).Value = S7_data_ResultLong.reals[29];
                SQLCommand.Parameters.Add("@real31", SqlDbType.Real).Value = S7_data_ResultLong.reals[30];
                SQLCommand.Parameters.Add("@real32", SqlDbType.Real).Value = S7_data_ResultLong.reals[31];
                SQLCommand.Parameters.Add("@real33", SqlDbType.Real).Value = S7_data_ResultLong.reals[32];
                SQLCommand.Parameters.Add("@real34", SqlDbType.Real).Value = S7_data_ResultLong.reals[33];
                SQLCommand.Parameters.Add("@real35", SqlDbType.Real).Value = S7_data_ResultLong.reals[34];
                SQLCommand.Parameters.Add("@real36", SqlDbType.Real).Value = S7_data_ResultLong.reals[35];
                SQLCommand.Parameters.Add("@real37", SqlDbType.Real).Value = S7_data_ResultLong.reals[36];
                SQLCommand.Parameters.Add("@real38", SqlDbType.Real).Value = S7_data_ResultLong.reals[37];
                SQLCommand.Parameters.Add("@real39", SqlDbType.Real).Value = S7_data_ResultLong.reals[38];
                SQLCommand.Parameters.Add("@real40", SqlDbType.Real).Value = S7_data_ResultLong.reals[39];
                SQLCommand.Parameters.Add("@real41", SqlDbType.Real).Value = S7_data_ResultLong.reals[40];
                SQLCommand.Parameters.Add("@real42", SqlDbType.Real).Value = S7_data_ResultLong.reals[41];
                SQLCommand.Parameters.Add("@real43", SqlDbType.Real).Value = S7_data_ResultLong.reals[42];
                SQLCommand.Parameters.Add("@real44", SqlDbType.Real).Value = S7_data_ResultLong.reals[43];
                SQLCommand.Parameters.Add("@real45", SqlDbType.Real).Value = S7_data_ResultLong.reals[44];
                SQLCommand.Parameters.Add("@real46", SqlDbType.Real).Value = S7_data_ResultLong.reals[45];
                SQLCommand.Parameters.Add("@real47", SqlDbType.Real).Value = S7_data_ResultLong.reals[46];
                SQLCommand.Parameters.Add("@real48", SqlDbType.Real).Value = S7_data_ResultLong.reals[47];
                SQLCommand.Parameters.Add("@real49", SqlDbType.Real).Value = S7_data_ResultLong.reals[48];
                SQLCommand.Parameters.Add("@real50", SqlDbType.Real).Value = S7_data_ResultLong.reals[49];
                SQLCommand.Parameters.Add("@real51", SqlDbType.Real).Value = S7_data_ResultLong.reals[50];
                SQLCommand.Parameters.Add("@real52", SqlDbType.Real).Value = S7_data_ResultLong.reals[51];
                SQLCommand.Parameters.Add("@real53", SqlDbType.Real).Value = S7_data_ResultLong.reals[52];
                SQLCommand.Parameters.Add("@real54", SqlDbType.Real).Value = S7_data_ResultLong.reals[53];
                SQLCommand.Parameters.Add("@real55", SqlDbType.Real).Value = S7_data_ResultLong.reals[54];
                SQLCommand.Parameters.Add("@real56", SqlDbType.Real).Value = S7_data_ResultLong.reals[55];
                SQLCommand.Parameters.Add("@real57", SqlDbType.Real).Value = S7_data_ResultLong.reals[56];
                SQLCommand.Parameters.Add("@real58", SqlDbType.Real).Value = S7_data_ResultLong.reals[57];
                SQLCommand.Parameters.Add("@real59", SqlDbType.Real).Value = S7_data_ResultLong.reals[58];
                SQLCommand.Parameters.Add("@real60", SqlDbType.Real).Value = S7_data_ResultLong.reals[59];
                SQLCommand.Parameters.Add("@real61", SqlDbType.Real).Value = S7_data_ResultLong.reals[60];
                SQLCommand.Parameters.Add("@real62", SqlDbType.Real).Value = S7_data_ResultLong.reals[61];
                SQLCommand.Parameters.Add("@real63", SqlDbType.Real).Value = S7_data_ResultLong.reals[62];
                SQLCommand.Parameters.Add("@real64", SqlDbType.Real).Value = S7_data_ResultLong.reals[63];
                SQLCommand.Parameters.Add("@real65", SqlDbType.Real).Value = S7_data_ResultLong.reals[64];
                SQLCommand.Parameters.Add("@real66", SqlDbType.Real).Value = S7_data_ResultLong.reals[65];
                SQLCommand.Parameters.Add("@real67", SqlDbType.Real).Value = S7_data_ResultLong.reals[66];
                SQLCommand.Parameters.Add("@real68", SqlDbType.Real).Value = S7_data_ResultLong.reals[67];
                SQLCommand.Parameters.Add("@real69", SqlDbType.Real).Value = S7_data_ResultLong.reals[68];
                SQLCommand.Parameters.Add("@real70", SqlDbType.Real).Value = S7_data_ResultLong.reals[69];
                SQLCommand.Parameters.Add("@real71", SqlDbType.Real).Value = S7_data_ResultLong.reals[70];
                SQLCommand.Parameters.Add("@real72", SqlDbType.Real).Value = S7_data_ResultLong.reals[71];
                SQLCommand.Parameters.Add("@real73", SqlDbType.Real).Value = S7_data_ResultLong.reals[72];
                SQLCommand.Parameters.Add("@real74", SqlDbType.Real).Value = S7_data_ResultLong.reals[73];
                SQLCommand.Parameters.Add("@real75", SqlDbType.Real).Value = S7_data_ResultLong.reals[74];
                SQLCommand.Parameters.Add("@real76", SqlDbType.Real).Value = S7_data_ResultLong.reals[75];
                SQLCommand.Parameters.Add("@real77", SqlDbType.Real).Value = S7_data_ResultLong.reals[76];
                SQLCommand.Parameters.Add("@real78", SqlDbType.Real).Value = S7_data_ResultLong.reals[77];
                SQLCommand.Parameters.Add("@real79", SqlDbType.Real).Value = S7_data_ResultLong.reals[78];
                SQLCommand.Parameters.Add("@real80", SqlDbType.Real).Value = S7_data_ResultLong.reals[79];
                SQLCommand.Parameters.Add("@real81", SqlDbType.Real).Value = S7_data_ResultLong.reals[80];
                SQLCommand.Parameters.Add("@real82", SqlDbType.Real).Value = S7_data_ResultLong.reals[81];
                SQLCommand.Parameters.Add("@real83", SqlDbType.Real).Value = S7_data_ResultLong.reals[82];
                SQLCommand.Parameters.Add("@real84", SqlDbType.Real).Value = S7_data_ResultLong.reals[83];
                SQLCommand.Parameters.Add("@real85", SqlDbType.Real).Value = S7_data_ResultLong.reals[84];
                SQLCommand.Parameters.Add("@real86", SqlDbType.Real).Value = S7_data_ResultLong.reals[85];
                SQLCommand.Parameters.Add("@real87", SqlDbType.Real).Value = S7_data_ResultLong.reals[86];
                SQLCommand.Parameters.Add("@real88", SqlDbType.Real).Value = S7_data_ResultLong.reals[87];
                SQLCommand.Parameters.Add("@real89", SqlDbType.Real).Value = S7_data_ResultLong.reals[88];
                SQLCommand.Parameters.Add("@real90", SqlDbType.Real).Value = S7_data_ResultLong.reals[89];
                SQLCommand.Parameters.Add("@real91", SqlDbType.Real).Value = S7_data_ResultLong.reals[90];
                SQLCommand.Parameters.Add("@real92", SqlDbType.Real).Value = S7_data_ResultLong.reals[91];
                SQLCommand.Parameters.Add("@real93", SqlDbType.Real).Value = S7_data_ResultLong.reals[92];
                SQLCommand.Parameters.Add("@real94", SqlDbType.Real).Value = S7_data_ResultLong.reals[93];
                SQLCommand.Parameters.Add("@real95", SqlDbType.Real).Value = S7_data_ResultLong.reals[94];
                SQLCommand.Parameters.Add("@real96", SqlDbType.Real).Value = S7_data_ResultLong.reals[95];
                SQLCommand.Parameters.Add("@real97", SqlDbType.Real).Value = S7_data_ResultLong.reals[96];
                SQLCommand.Parameters.Add("@real98", SqlDbType.Real).Value = S7_data_ResultLong.reals[97];
                SQLCommand.Parameters.Add("@real99", SqlDbType.Real).Value = S7_data_ResultLong.reals[98];
                SQLCommand.Parameters.Add("@real100", SqlDbType.Real).Value = S7_data_ResultLong.reals[99];
                SQLCommand.Parameters.Add("@dtl1", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultLong.dtls[0].Year, S7_data_ResultLong.dtls[0].Month, S7_data_ResultLong.dtls[0].Day, S7_data_ResultLong.dtls[0].Hour, S7_data_ResultLong.dtls[0].Minute, S7_data_ResultLong.dtls[0].Second);
                SQLCommand.Parameters.Add("@dtl2", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultLong.dtls[1].Year, S7_data_ResultLong.dtls[1].Month, S7_data_ResultLong.dtls[1].Day, S7_data_ResultLong.dtls[1].Hour, S7_data_ResultLong.dtls[1].Minute, S7_data_ResultLong.dtls[1].Second);
                SQLCommand.Parameters.Add("@dtl3", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultLong.dtls[2].Year, S7_data_ResultLong.dtls[2].Month, S7_data_ResultLong.dtls[2].Day, S7_data_ResultLong.dtls[2].Hour, S7_data_ResultLong.dtls[2].Minute, S7_data_ResultLong.dtls[2].Second);
                SQLCommand.Parameters.Add("@dtl4", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultLong.dtls[3].Year, S7_data_ResultLong.dtls[3].Month, S7_data_ResultLong.dtls[3].Day, S7_data_ResultLong.dtls[3].Hour, S7_data_ResultLong.dtls[3].Minute, S7_data_ResultLong.dtls[3].Second);
                SQLCommand.Parameters.Add("@dtl5", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultLong.dtls[4].Year, S7_data_ResultLong.dtls[4].Month, S7_data_ResultLong.dtls[4].Day, S7_data_ResultLong.dtls[4].Hour, S7_data_ResultLong.dtls[4].Minute, S7_data_ResultLong.dtls[4].Second);
                SQLCommand.Parameters.Add("@string1", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.strings[0].ToString();
                SQLCommand.Parameters.Add("@string2", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.strings[1].ToString();
                SQLCommand.Parameters.Add("@string3", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.strings[2].ToString();
                SQLCommand.Parameters.Add("@string4", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.strings[3].ToString();
                SQLCommand.Parameters.Add("@string5", SqlDbType.VarChar, 256).Value = S7_data_ResultLong.strings[4].ToString();

                SQLCommand.ExecuteNonQuery();

                SQLConnection.Close();

                S7_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (SqlException e)
            {
                S7_data_Write.Task_Confirm_From_PC = 2;
                S7_data_Write.Error_Status = 4;
                LogEvent($"ReadParametersAndSaveToDatabaseLongSiemens | SQL | {e.Message}");
            }
            catch (Exception e)
            {
                S7_data_Write.Task_Confirm_From_PC = 2;
                if (e.Message == "No DMC Code")
                {
                    S7_data_Write.Error_Status = 1;
                }
                else
                {
                    S7_data_Write.Error_Status = 9;
                }
                LogEvent($"ReadParametersAndSaveToDatabaseLongSiemens | {e.Message}");
                IsConnected = false;
            }

            LogEvent("ReadParametersAndSaveToDatabaseLongSiemens | END");
        }

        private void ReadParametersAndSaveToDatabaseShortSiemens()
        {
            LogEvent("ReadParametersAndSaveToDatabaseShortSiemens | START");

            try {
                ReadResultShortFromPLCSiemens();

                if (S7_data_ResultShort.DMC_Code1.ToString() == "" && S7_data_ResultShort.DMC_Code2.ToString() == "")
                {
                    throw new Exception("No DMC Code");
                }

                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "INSERT INTO dbo.logs " +
                                            "(machine_id, dmc_code1, dmc_code2, operation_result1, operation_result2, operation_datetime1, operation_datetime2, reference, cycle_time, operator, int_1, int_2, int_3, int_4, int_5, int_6, int_7, int_8, int_9, int_10, real_1, real_2, real_3, real_4, real_5, real_6, real_7, real_8, real_9, real_10, dtl_1, dtl_2, dtl_3, string_1, string_2, string_3, string_4, string_5, string_6, string_7) " +
                                            "VALUES " +
                                            "(@machineID, @dmcCode1, @dmcCode2, @operationResult1, @operationResult2, @operationDatetime1, @operationDatetime2, @reference, @cycleTime, @operator, @int1, @int2, @int3, @int4, @int5, @int6, @int7, @int8, @int9, @int10, @real1, @real2, @real3, @real4, @real5, @real6, @real7, @real8, @real9, @real10, @dtl1, @dtl2, @dtl3, @string1, @string2, @string3, @string4, @string5, @string6, @string7)";

                SQLCommand.Parameters.Add("@machineID", SqlDbType.Int).Value = ID;
                SQLCommand.Parameters.Add("@dmcCode1", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.DMC_Code1.ToString();
                SQLCommand.Parameters.Add("@dmcCode2", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.DMC_Code2.ToString();
                SQLCommand.Parameters.Add("@operationResult1", SqlDbType.Int).Value = S7_data_ResultShort.Operation_Result1;
                SQLCommand.Parameters.Add("@operationResult2", SqlDbType.Int).Value = S7_data_ResultShort.Operation_Result2;
                SQLCommand.Parameters.Add("@operationDatetime1", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultShort.Operation_DateTime1.Year, S7_data_ResultShort.Operation_DateTime1.Month, S7_data_ResultShort.Operation_DateTime1.Day, S7_data_ResultShort.Operation_DateTime1.Hour, S7_data_ResultShort.Operation_DateTime1.Minute, S7_data_ResultShort.Operation_DateTime1.Second);
                //SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultShort.Operation_DateTime2.Year, S7_data_ResultShort.Operation_DateTime2.Month, S7_data_ResultShort.Operation_DateTime2.Day, S7_data_ResultShort.Operation_DateTime2.Hour, S7_data_ResultShort.Operation_DateTime2.Minute, S7_data_ResultShort.Operation_DateTime2.Second);
                SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = DateTime.Now;
                SQLCommand.Parameters.Add("@reference", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.Reference.ToString();
                SQLCommand.Parameters.Add("@cycleTime", SqlDbType.Int).Value = S7_data_ResultShort.Cycle_Time;
                SQLCommand.Parameters.Add("@operator", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.Operator.ToString();
                SQLCommand.Parameters.Add("@int1", SqlDbType.Int).Value = S7_data_ResultShort.dints[0];
                SQLCommand.Parameters.Add("@int2", SqlDbType.Int).Value = S7_data_ResultShort.dints[1];
                SQLCommand.Parameters.Add("@int3", SqlDbType.Int).Value = S7_data_ResultShort.dints[2];
                SQLCommand.Parameters.Add("@int4", SqlDbType.Int).Value = S7_data_ResultShort.dints[3];
                SQLCommand.Parameters.Add("@int5", SqlDbType.Int).Value = S7_data_ResultShort.dints[4];
                SQLCommand.Parameters.Add("@int6", SqlDbType.Int).Value = S7_data_ResultShort.dints[5];
                SQLCommand.Parameters.Add("@int7", SqlDbType.Int).Value = S7_data_ResultShort.dints[6];
                SQLCommand.Parameters.Add("@int8", SqlDbType.Int).Value = S7_data_ResultShort.dints[7];
                SQLCommand.Parameters.Add("@int9", SqlDbType.Int).Value = S7_data_ResultShort.dints[8];
                SQLCommand.Parameters.Add("@int10", SqlDbType.Int).Value = S7_data_ResultShort.dints[9];
                SQLCommand.Parameters.Add("@real1", SqlDbType.Real).Value = S7_data_ResultShort.reals[0];
                SQLCommand.Parameters.Add("@real2", SqlDbType.Real).Value = S7_data_ResultShort.reals[1];
                SQLCommand.Parameters.Add("@real3", SqlDbType.Real).Value = S7_data_ResultShort.reals[2];
                SQLCommand.Parameters.Add("@real4", SqlDbType.Real).Value = S7_data_ResultShort.reals[3];
                SQLCommand.Parameters.Add("@real5", SqlDbType.Real).Value = S7_data_ResultShort.reals[4];
                SQLCommand.Parameters.Add("@real6", SqlDbType.Real).Value = S7_data_ResultShort.reals[5];
                SQLCommand.Parameters.Add("@real7", SqlDbType.Real).Value = S7_data_ResultShort.reals[6];
                SQLCommand.Parameters.Add("@real8", SqlDbType.Real).Value = S7_data_ResultShort.reals[7];
                SQLCommand.Parameters.Add("@real9", SqlDbType.Real).Value = S7_data_ResultShort.reals[8];
                SQLCommand.Parameters.Add("@real10", SqlDbType.Real).Value = S7_data_ResultShort.reals[9];
                SQLCommand.Parameters.Add("@dtl1", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultShort.dtls[0].Year, S7_data_ResultShort.dtls[0].Month, S7_data_ResultShort.dtls[0].Day, S7_data_ResultShort.dtls[0].Hour, S7_data_ResultShort.dtls[0].Minute, S7_data_ResultShort.dtls[0].Second);
                SQLCommand.Parameters.Add("@dtl2", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultShort.dtls[1].Year, S7_data_ResultShort.dtls[1].Month, S7_data_ResultShort.dtls[1].Day, S7_data_ResultShort.dtls[1].Hour, S7_data_ResultShort.dtls[1].Minute, S7_data_ResultShort.dtls[1].Second);
                SQLCommand.Parameters.Add("@dtl3", SqlDbType.DateTime).Value = new DateTime(S7_data_ResultShort.dtls[2].Year, S7_data_ResultShort.dtls[2].Month, S7_data_ResultShort.dtls[2].Day, S7_data_ResultShort.dtls[2].Hour, S7_data_ResultShort.dtls[2].Minute, S7_data_ResultShort.dtls[2].Second);
                SQLCommand.Parameters.Add("@string1", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.strings[0].ToString();
                SQLCommand.Parameters.Add("@string2", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.strings[1].ToString();
                SQLCommand.Parameters.Add("@string3", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.strings[2].ToString();
                SQLCommand.Parameters.Add("@string4", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.strings[3].ToString();
                SQLCommand.Parameters.Add("@string5", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.strings[4].ToString();
                SQLCommand.Parameters.Add("@string6", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.strings[5].ToString();
                SQLCommand.Parameters.Add("@string7", SqlDbType.VarChar, 256).Value = S7_data_ResultShort.strings[6].ToString();

                SQLCommand.ExecuteNonQuery();

                SQLConnection.Close();

                S7_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (SqlException e)
            {
                S7_data_Write.Task_Confirm_From_PC = 2;
                S7_data_Write.Error_Status = 4;
                LogEvent($"ReadParametersAndSaveToDatabaseShortSiemens | SQL | {e.Message}");
            }
            catch (Exception e)
            {
                S7_data_Write.Task_Confirm_From_PC = 2;
                if (e.Message == "No DMC Code")
                {
                    S7_data_Write.Error_Status = 1;
                }
                else
                {
                    S7_data_Write.Error_Status = 9;
                }
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
            try {
                ReadResultLongFromPLCAB();

                if (AB_data_ResultLong.DMC_Code1.ToString() == "" && AB_data_ResultLong.DMC_Code2.ToString() == "")
                {
                    throw new Exception("No DMC Code");
                }

                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "INSERT INTO dbo.logs " +
                                            "(machine_id, dmc_code1, dmc_code2, operation_result1, operation_result2, operation_datetime1, operation_datetime2, reference, cycle_time, operator, int_1, int_2, int_3, int_4, int_5, int_6, int_7, int_8, int_9, int_10, real_1, real_2, real_3, real_4, real_5, real_6, real_7, real_8, real_9, real_10, real_11, real_12, real_13, real_14, real_15, real_16, real_17, real_18, real_19, real_20, real_21, real_22, real_23, real_24, real_25, real_26, real_27, real_28, real_29, real_30, real_31, real_32, real_33, real_34, real_35, real_36, real_37, real_38, real_39, real_40, real_41, real_42, real_43, real_44, real_45, real_46, real_47, real_48, real_49, real_50, real_51, real_52, real_53, real_54, real_55, real_56, real_57, real_58, real_59, real_60, real_61, real_62, real_63, real_64, real_65, real_66, real_67, real_68, real_69, real_70, real_71, real_72, real_73, real_74, real_75, real_76, real_77, real_78, real_79, real_80, real_81, real_82, real_83, real_84, real_85, real_86, real_87, real_88, real_89, real_90, real_91, real_92, real_93, real_94, real_95, real_96, real_97, real_98, real_99, real_100, dtl_1, dtl_2, dtl_3, dtl_4, dtl_5, string_1, string_2, string_3, string_4, string_5) " +
                                            "VALUES " +
                                            "(@machineID, @dmcCode1, @dmcCode2, @operationResult1, @operationResult2, @operationDatetime1, @operationDatetime2, @reference, @cycleTime, @operator, @int1, @int2, @int3, @int4, @int5, @int6, @int7, @int8, @int9, @int10, @real1, @real2, @real3, @real4, @real5, @real6, @real7, @real8, @real9, @real10, @real11, @real12, @real13, @real14, @real15, @real16, @real17, @real18, @real19, @real20, @real21, @real22, @real23, @real24, @real25, @real26, @real27, @real28, @real29, @real30, @real31, @real32, @real33, @real34, @real35, @real36, @real37, @real38, @real39, @real40, @real41, @real42, @real43, @real44, @real45, @real46, @real47, @real48, @real49, @real50, @real51, @real52, @real53, @real54, @real55, @real56, @real57, @real58, @real59, @real60, @real61, @real62, @real63, @real64, @real65, @real66, @real67, @real68, @real69, @real70, @real71, @real72, @real73, @real74, @real75, @real76, @real77, @real78, @real79, @real80, @real81, @real82, @real83, @real84, @real85, @real86, @real87, @real88, @real89, @real90, @real91, @real92, @real93, @real94, @real95, @real96, @real97, @real98, @real99, @real100, @dtl1, @dtl2, @dtl3, @dtl4, @dtl5, @string1, @string2, @string3, @string4, @string5)";

                SQLCommand.Parameters.Add("@machineID", SqlDbType.Int).Value = ID;
                SQLCommand.Parameters.Add("@dmcCode1", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.DMC_Code1.ToString();
                SQLCommand.Parameters.Add("@dmcCode2", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.DMC_Code2.ToString();
                SQLCommand.Parameters.Add("@operationResult1", SqlDbType.Int).Value = AB_data_ResultLong.Operation_Result1;
                SQLCommand.Parameters.Add("@operationResult2", SqlDbType.Int).Value = AB_data_ResultLong.Operation_Result2;
                SQLCommand.Parameters.Add("@operationDatetime1", SqlDbType.DateTime).Value = AB_data_ResultLong.Operation_DateTime1.ToDateTime();
                //SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = AB_data_ResultLong.Operation_DateTime2.ToDateTime();
                SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = DateTime.Now;
                SQLCommand.Parameters.Add("@reference", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.Reference.ToString();
                SQLCommand.Parameters.Add("@cycleTime", SqlDbType.Int).Value = AB_data_ResultLong.Cycle_Time;
                SQLCommand.Parameters.Add("@operator", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.Operator.ToString();
                SQLCommand.Parameters.Add("@int1", SqlDbType.Int).Value = AB_data_ResultLong.dints[0];
                SQLCommand.Parameters.Add("@int2", SqlDbType.Int).Value = AB_data_ResultLong.dints[1];
                SQLCommand.Parameters.Add("@int3", SqlDbType.Int).Value = AB_data_ResultLong.dints[2];
                SQLCommand.Parameters.Add("@int4", SqlDbType.Int).Value = AB_data_ResultLong.dints[3];
                SQLCommand.Parameters.Add("@int5", SqlDbType.Int).Value = AB_data_ResultLong.dints[4];
                SQLCommand.Parameters.Add("@int6", SqlDbType.Int).Value = AB_data_ResultLong.dints[5];
                SQLCommand.Parameters.Add("@int7", SqlDbType.Int).Value = AB_data_ResultLong.dints[6];
                SQLCommand.Parameters.Add("@int8", SqlDbType.Int).Value = AB_data_ResultLong.dints[7];
                SQLCommand.Parameters.Add("@int9", SqlDbType.Int).Value = AB_data_ResultLong.dints[8];
                SQLCommand.Parameters.Add("@int10", SqlDbType.Int).Value = AB_data_ResultLong.dints[9];
                SQLCommand.Parameters.Add("@real1", SqlDbType.Real).Value = AB_data_ResultLong.reals[0];
                SQLCommand.Parameters.Add("@real2", SqlDbType.Real).Value = AB_data_ResultLong.reals[1];
                SQLCommand.Parameters.Add("@real3", SqlDbType.Real).Value = AB_data_ResultLong.reals[2];
                SQLCommand.Parameters.Add("@real4", SqlDbType.Real).Value = AB_data_ResultLong.reals[3];
                SQLCommand.Parameters.Add("@real5", SqlDbType.Real).Value = AB_data_ResultLong.reals[4];
                SQLCommand.Parameters.Add("@real6", SqlDbType.Real).Value = AB_data_ResultLong.reals[5];
                SQLCommand.Parameters.Add("@real7", SqlDbType.Real).Value = AB_data_ResultLong.reals[6];
                SQLCommand.Parameters.Add("@real8", SqlDbType.Real).Value = AB_data_ResultLong.reals[7];
                SQLCommand.Parameters.Add("@real9", SqlDbType.Real).Value = AB_data_ResultLong.reals[8];
                SQLCommand.Parameters.Add("@real10", SqlDbType.Real).Value = AB_data_ResultLong.reals[9];
                SQLCommand.Parameters.Add("@real11", SqlDbType.Real).Value = AB_data_ResultLong.reals[10];
                SQLCommand.Parameters.Add("@real12", SqlDbType.Real).Value = AB_data_ResultLong.reals[11];
                SQLCommand.Parameters.Add("@real13", SqlDbType.Real).Value = AB_data_ResultLong.reals[12];
                SQLCommand.Parameters.Add("@real14", SqlDbType.Real).Value = AB_data_ResultLong.reals[13];
                SQLCommand.Parameters.Add("@real15", SqlDbType.Real).Value = AB_data_ResultLong.reals[14];
                SQLCommand.Parameters.Add("@real16", SqlDbType.Real).Value = AB_data_ResultLong.reals[15];
                SQLCommand.Parameters.Add("@real17", SqlDbType.Real).Value = AB_data_ResultLong.reals[16];
                SQLCommand.Parameters.Add("@real18", SqlDbType.Real).Value = AB_data_ResultLong.reals[17];
                SQLCommand.Parameters.Add("@real19", SqlDbType.Real).Value = AB_data_ResultLong.reals[18];
                SQLCommand.Parameters.Add("@real20", SqlDbType.Real).Value = AB_data_ResultLong.reals[19];
                SQLCommand.Parameters.Add("@real21", SqlDbType.Real).Value = AB_data_ResultLong.reals[20];
                SQLCommand.Parameters.Add("@real22", SqlDbType.Real).Value = AB_data_ResultLong.reals[21];
                SQLCommand.Parameters.Add("@real23", SqlDbType.Real).Value = AB_data_ResultLong.reals[22];
                SQLCommand.Parameters.Add("@real24", SqlDbType.Real).Value = AB_data_ResultLong.reals[23];
                SQLCommand.Parameters.Add("@real25", SqlDbType.Real).Value = AB_data_ResultLong.reals[24];
                SQLCommand.Parameters.Add("@real26", SqlDbType.Real).Value = AB_data_ResultLong.reals[25];
                SQLCommand.Parameters.Add("@real27", SqlDbType.Real).Value = AB_data_ResultLong.reals[26];
                SQLCommand.Parameters.Add("@real28", SqlDbType.Real).Value = AB_data_ResultLong.reals[27];
                SQLCommand.Parameters.Add("@real29", SqlDbType.Real).Value = AB_data_ResultLong.reals[28];
                SQLCommand.Parameters.Add("@real30", SqlDbType.Real).Value = AB_data_ResultLong.reals[29];
                SQLCommand.Parameters.Add("@real31", SqlDbType.Real).Value = AB_data_ResultLong.reals[30];
                SQLCommand.Parameters.Add("@real32", SqlDbType.Real).Value = AB_data_ResultLong.reals[31];
                SQLCommand.Parameters.Add("@real33", SqlDbType.Real).Value = AB_data_ResultLong.reals[32];
                SQLCommand.Parameters.Add("@real34", SqlDbType.Real).Value = AB_data_ResultLong.reals[33];
                SQLCommand.Parameters.Add("@real35", SqlDbType.Real).Value = AB_data_ResultLong.reals[34];
                SQLCommand.Parameters.Add("@real36", SqlDbType.Real).Value = AB_data_ResultLong.reals[35];
                SQLCommand.Parameters.Add("@real37", SqlDbType.Real).Value = AB_data_ResultLong.reals[36];
                SQLCommand.Parameters.Add("@real38", SqlDbType.Real).Value = AB_data_ResultLong.reals[37];
                SQLCommand.Parameters.Add("@real39", SqlDbType.Real).Value = AB_data_ResultLong.reals[38];
                SQLCommand.Parameters.Add("@real40", SqlDbType.Real).Value = AB_data_ResultLong.reals[39];
                SQLCommand.Parameters.Add("@real41", SqlDbType.Real).Value = AB_data_ResultLong.reals[40];
                SQLCommand.Parameters.Add("@real42", SqlDbType.Real).Value = AB_data_ResultLong.reals[41];
                SQLCommand.Parameters.Add("@real43", SqlDbType.Real).Value = AB_data_ResultLong.reals[42];
                SQLCommand.Parameters.Add("@real44", SqlDbType.Real).Value = AB_data_ResultLong.reals[43];
                SQLCommand.Parameters.Add("@real45", SqlDbType.Real).Value = AB_data_ResultLong.reals[44];
                SQLCommand.Parameters.Add("@real46", SqlDbType.Real).Value = AB_data_ResultLong.reals[45];
                SQLCommand.Parameters.Add("@real47", SqlDbType.Real).Value = AB_data_ResultLong.reals[46];
                SQLCommand.Parameters.Add("@real48", SqlDbType.Real).Value = AB_data_ResultLong.reals[47];
                SQLCommand.Parameters.Add("@real49", SqlDbType.Real).Value = AB_data_ResultLong.reals[48];
                SQLCommand.Parameters.Add("@real50", SqlDbType.Real).Value = AB_data_ResultLong.reals[49];
                SQLCommand.Parameters.Add("@real51", SqlDbType.Real).Value = AB_data_ResultLong.reals[50];
                SQLCommand.Parameters.Add("@real52", SqlDbType.Real).Value = AB_data_ResultLong.reals[51];
                SQLCommand.Parameters.Add("@real53", SqlDbType.Real).Value = AB_data_ResultLong.reals[52];
                SQLCommand.Parameters.Add("@real54", SqlDbType.Real).Value = AB_data_ResultLong.reals[53];
                SQLCommand.Parameters.Add("@real55", SqlDbType.Real).Value = AB_data_ResultLong.reals[54];
                SQLCommand.Parameters.Add("@real56", SqlDbType.Real).Value = AB_data_ResultLong.reals[55];
                SQLCommand.Parameters.Add("@real57", SqlDbType.Real).Value = AB_data_ResultLong.reals[56];
                SQLCommand.Parameters.Add("@real58", SqlDbType.Real).Value = AB_data_ResultLong.reals[57];
                SQLCommand.Parameters.Add("@real59", SqlDbType.Real).Value = AB_data_ResultLong.reals[58];
                SQLCommand.Parameters.Add("@real60", SqlDbType.Real).Value = AB_data_ResultLong.reals[59];
                SQLCommand.Parameters.Add("@real61", SqlDbType.Real).Value = AB_data_ResultLong.reals[60];
                SQLCommand.Parameters.Add("@real62", SqlDbType.Real).Value = AB_data_ResultLong.reals[61];
                SQLCommand.Parameters.Add("@real63", SqlDbType.Real).Value = AB_data_ResultLong.reals[62];
                SQLCommand.Parameters.Add("@real64", SqlDbType.Real).Value = AB_data_ResultLong.reals[63];
                SQLCommand.Parameters.Add("@real65", SqlDbType.Real).Value = AB_data_ResultLong.reals[64];
                SQLCommand.Parameters.Add("@real66", SqlDbType.Real).Value = AB_data_ResultLong.reals[65];
                SQLCommand.Parameters.Add("@real67", SqlDbType.Real).Value = AB_data_ResultLong.reals[66];
                SQLCommand.Parameters.Add("@real68", SqlDbType.Real).Value = AB_data_ResultLong.reals[67];
                SQLCommand.Parameters.Add("@real69", SqlDbType.Real).Value = AB_data_ResultLong.reals[68];
                SQLCommand.Parameters.Add("@real70", SqlDbType.Real).Value = AB_data_ResultLong.reals[69];
                SQLCommand.Parameters.Add("@real71", SqlDbType.Real).Value = AB_data_ResultLong.reals[70];
                SQLCommand.Parameters.Add("@real72", SqlDbType.Real).Value = AB_data_ResultLong.reals[71];
                SQLCommand.Parameters.Add("@real73", SqlDbType.Real).Value = AB_data_ResultLong.reals[72];
                SQLCommand.Parameters.Add("@real74", SqlDbType.Real).Value = AB_data_ResultLong.reals[73];
                SQLCommand.Parameters.Add("@real75", SqlDbType.Real).Value = AB_data_ResultLong.reals[74];
                SQLCommand.Parameters.Add("@real76", SqlDbType.Real).Value = AB_data_ResultLong.reals[75];
                SQLCommand.Parameters.Add("@real77", SqlDbType.Real).Value = AB_data_ResultLong.reals[76];
                SQLCommand.Parameters.Add("@real78", SqlDbType.Real).Value = AB_data_ResultLong.reals[77];
                SQLCommand.Parameters.Add("@real79", SqlDbType.Real).Value = AB_data_ResultLong.reals[78];
                SQLCommand.Parameters.Add("@real80", SqlDbType.Real).Value = AB_data_ResultLong.reals[79];
                SQLCommand.Parameters.Add("@real81", SqlDbType.Real).Value = AB_data_ResultLong.reals[80];
                SQLCommand.Parameters.Add("@real82", SqlDbType.Real).Value = AB_data_ResultLong.reals[81];
                SQLCommand.Parameters.Add("@real83", SqlDbType.Real).Value = AB_data_ResultLong.reals[82];
                SQLCommand.Parameters.Add("@real84", SqlDbType.Real).Value = AB_data_ResultLong.reals[83];
                SQLCommand.Parameters.Add("@real85", SqlDbType.Real).Value = AB_data_ResultLong.reals[84];
                SQLCommand.Parameters.Add("@real86", SqlDbType.Real).Value = AB_data_ResultLong.reals[85];
                SQLCommand.Parameters.Add("@real87", SqlDbType.Real).Value = AB_data_ResultLong.reals[86];
                SQLCommand.Parameters.Add("@real88", SqlDbType.Real).Value = AB_data_ResultLong.reals[87];
                SQLCommand.Parameters.Add("@real89", SqlDbType.Real).Value = AB_data_ResultLong.reals[88];
                SQLCommand.Parameters.Add("@real90", SqlDbType.Real).Value = AB_data_ResultLong.reals[89];
                SQLCommand.Parameters.Add("@real91", SqlDbType.Real).Value = AB_data_ResultLong.reals[90];
                SQLCommand.Parameters.Add("@real92", SqlDbType.Real).Value = AB_data_ResultLong.reals[91];
                SQLCommand.Parameters.Add("@real93", SqlDbType.Real).Value = AB_data_ResultLong.reals[92];
                SQLCommand.Parameters.Add("@real94", SqlDbType.Real).Value = AB_data_ResultLong.reals[93];
                SQLCommand.Parameters.Add("@real95", SqlDbType.Real).Value = AB_data_ResultLong.reals[94];
                SQLCommand.Parameters.Add("@real96", SqlDbType.Real).Value = AB_data_ResultLong.reals[95];
                SQLCommand.Parameters.Add("@real97", SqlDbType.Real).Value = AB_data_ResultLong.reals[96];
                SQLCommand.Parameters.Add("@real98", SqlDbType.Real).Value = AB_data_ResultLong.reals[97];
                SQLCommand.Parameters.Add("@real99", SqlDbType.Real).Value = AB_data_ResultLong.reals[98];
                SQLCommand.Parameters.Add("@real100", SqlDbType.Real).Value = AB_data_ResultLong.reals[99];
                SQLCommand.Parameters.Add("@dtl1", SqlDbType.DateTime).Value = AB_data_ResultLong.dtls[0].ToDateTime();
                SQLCommand.Parameters.Add("@dtl2", SqlDbType.DateTime).Value = AB_data_ResultLong.dtls[1].ToDateTime();
                SQLCommand.Parameters.Add("@dtl3", SqlDbType.DateTime).Value = AB_data_ResultLong.dtls[2].ToDateTime();
                SQLCommand.Parameters.Add("@dtl4", SqlDbType.DateTime).Value = AB_data_ResultLong.dtls[3].ToDateTime();
                SQLCommand.Parameters.Add("@dtl5", SqlDbType.DateTime).Value = AB_data_ResultLong.dtls[4].ToDateTime();
                SQLCommand.Parameters.Add("@string1", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.strings[0].ToString();
                SQLCommand.Parameters.Add("@string2", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.strings[1].ToString();
                SQLCommand.Parameters.Add("@string3", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.strings[2].ToString();
                SQLCommand.Parameters.Add("@string4", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.strings[3].ToString();
                SQLCommand.Parameters.Add("@string5", SqlDbType.VarChar, 256).Value = AB_data_ResultLong.strings[4].ToString();

                SQLCommand.ExecuteNonQuery();

                SQLConnection.Close();

                AB_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (SqlException e)
            {
                LogEvent($"ReadParametersAndSaveToDatabaseLongAB | SQL | {e.Message}");
            }
            catch (Exception e)
            {
                AB_data_Write.Task_Confirm_From_PC = 2;
                if (e.Message == "No DMC Code")
                {
                    AB_data_Write.Error_Status = 1;
                }
                else
                {
                    AB_data_Write.Error_Status = 9;
                }
                LogEvent($"ReadParametersAndSaveToDatabaseLongAB | {e.Message}");
                IsConnected = false;
            }

            LogEvent("ReadParametersAndSaveToDatabaseLongAB | END");
        }

        private void ReadParametersAndSaveToDatabaseShortAB()
        {
            LogEvent("ReadParametersAndSaveToDatabaseShortAB | START");
            try {
                ReadResultShortFromPLCAB();

                if (AB_data_ResultShort.DMC_Code1.ToString() == "" && AB_data_ResultShort.DMC_Code2.ToString() == "")
                {
                    throw new Exception("No DMC Code");
                }

                SqlConnection SQLConnection = new SqlConnection(@"Data source=" + DBServer + "," + DBPort + ";Initial Catalog=" + DBDatabase + ";User ID=" + DBUser + ";Password=" + DBPassword + ";");
                SQLConnection.Open();
                SqlCommand SQLCommand = SQLConnection.CreateCommand();

                SQLCommand.CommandText = "INSERT INTO dbo.logs " +
                                            "(machine_id, dmc_code1, dmc_code2, operation_result1, operation_result2, operation_datetime1, operation_datetime2, reference, cycle_time, operator, int_1, int_2, int_3, int_4, int_5, int_6, int_7, int_8, int_9, int_10, real_1, real_2, real_3, real_4, real_5, real_6, real_7, real_8, real_9, real_10, dtl_1, dtl_2, dtl_3, string_1, string_2, string_3, string_4, string_5, string_6, string_7) " +
                                            "VALUES " +
                                            "(@machineID, @dmcCode1, @dmcCode2, @operationResult1, @operationResult2, @operationDatetime1, @operationDatetime2, @reference, @cycleTime, @operator, @int1, @int2, @int3, @int4, @int5, @int6, @int7, @int8, @int9, @int10, @real1, @real2, @real3, @real4, @real5, @real6, @real7, @real8, @real9, @real10, @dtl1, @dtl2, @dtl3, @string1, @string2, @string3, @string4, @string5, @string6, @string7)";

                SQLCommand.Parameters.Add("@machineID", SqlDbType.Int).Value = ID;
                SQLCommand.Parameters.Add("@dmcCode1", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.DMC_Code1.ToString();
                SQLCommand.Parameters.Add("@dmcCode2", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.DMC_Code2.ToString();
                SQLCommand.Parameters.Add("@operationResult1", SqlDbType.Int).Value = AB_data_ResultShort.Operation_Result1;
                SQLCommand.Parameters.Add("@operationResult2", SqlDbType.Int).Value = AB_data_ResultShort.Operation_Result2;
                SQLCommand.Parameters.Add("@operationDatetime1", SqlDbType.DateTime).Value = AB_data_ResultShort.Operation_DateTime1.ToDateTime();
                //SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = AB_data_ResultShort.Operation_DateTime2.ToDateTime();
                SQLCommand.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = DateTime.Now;
                SQLCommand.Parameters.Add("@reference", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.Reference.ToString();
                SQLCommand.Parameters.Add("@cycleTime", SqlDbType.Int).Value = AB_data_ResultShort.Cycle_Time;
                SQLCommand.Parameters.Add("@operator", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.Operator.ToString();
                SQLCommand.Parameters.Add("@int1", SqlDbType.Int).Value = AB_data_ResultShort.dints[0];
                SQLCommand.Parameters.Add("@int2", SqlDbType.Int).Value = AB_data_ResultShort.dints[1];
                SQLCommand.Parameters.Add("@int3", SqlDbType.Int).Value = AB_data_ResultShort.dints[2];
                SQLCommand.Parameters.Add("@int4", SqlDbType.Int).Value = AB_data_ResultShort.dints[3];
                SQLCommand.Parameters.Add("@int5", SqlDbType.Int).Value = AB_data_ResultShort.dints[4];
                SQLCommand.Parameters.Add("@int6", SqlDbType.Int).Value = AB_data_ResultShort.dints[5];
                SQLCommand.Parameters.Add("@int7", SqlDbType.Int).Value = AB_data_ResultShort.dints[6];
                SQLCommand.Parameters.Add("@int8", SqlDbType.Int).Value = AB_data_ResultShort.dints[7];
                SQLCommand.Parameters.Add("@int9", SqlDbType.Int).Value = AB_data_ResultShort.dints[8];
                SQLCommand.Parameters.Add("@int10", SqlDbType.Int).Value = AB_data_ResultShort.dints[9];
                SQLCommand.Parameters.Add("@real1", SqlDbType.Real).Value = AB_data_ResultShort.reals[0];
                SQLCommand.Parameters.Add("@real2", SqlDbType.Real).Value = AB_data_ResultShort.reals[1];
                SQLCommand.Parameters.Add("@real3", SqlDbType.Real).Value = AB_data_ResultShort.reals[2];
                SQLCommand.Parameters.Add("@real4", SqlDbType.Real).Value = AB_data_ResultShort.reals[3];
                SQLCommand.Parameters.Add("@real5", SqlDbType.Real).Value = AB_data_ResultShort.reals[4];
                SQLCommand.Parameters.Add("@real6", SqlDbType.Real).Value = AB_data_ResultShort.reals[5];
                SQLCommand.Parameters.Add("@real7", SqlDbType.Real).Value = AB_data_ResultShort.reals[6];
                SQLCommand.Parameters.Add("@real8", SqlDbType.Real).Value = AB_data_ResultShort.reals[7];
                SQLCommand.Parameters.Add("@real9", SqlDbType.Real).Value = AB_data_ResultShort.reals[8];
                SQLCommand.Parameters.Add("@real10", SqlDbType.Real).Value = AB_data_ResultShort.reals[9];
                SQLCommand.Parameters.Add("@dtl1", SqlDbType.DateTime).Value = AB_data_ResultShort.dtls[0].ToDateTime();
                SQLCommand.Parameters.Add("@dtl2", SqlDbType.DateTime).Value = AB_data_ResultShort.dtls[1].ToDateTime();
                SQLCommand.Parameters.Add("@dtl3", SqlDbType.DateTime).Value = AB_data_ResultShort.dtls[2].ToDateTime();
                SQLCommand.Parameters.Add("@string1", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.strings[0].ToString();
                SQLCommand.Parameters.Add("@string2", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.strings[1].ToString();
                SQLCommand.Parameters.Add("@string3", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.strings[2].ToString();
                SQLCommand.Parameters.Add("@string4", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.strings[3].ToString();
                SQLCommand.Parameters.Add("@string5", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.strings[4].ToString();
                SQLCommand.Parameters.Add("@string6", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.strings[5].ToString();
                SQLCommand.Parameters.Add("@string7", SqlDbType.VarChar, 256).Value = AB_data_ResultShort.strings[6].ToString();

                SQLCommand.ExecuteNonQuery();

                SQLConnection.Close();

                AB_data_Write.Task_Confirm_From_PC = 2;
            }
            catch (SqlException e)
            {
                AB_data_Write.Task_Confirm_From_PC = 2;
                AB_data_Write.Error_Status = 4;
                LogEvent($"ReadParametersAndSaveToDatabaseShortAB | SQL | {e.Message}");
            }
            catch (Exception e)
            {
                AB_data_Write.Task_Confirm_From_PC = 2;
                if (e.Message == "No DMC Code") {
                    AB_data_Write.Error_Status = 1;
                }
                else {
                    AB_data_Write.Error_Status = 9;
                }
                LogEvent($"ReadParametersAndSaveToDatabaseShortAB | {e.Message}");
                IsConnected = false;
            }

            LogEvent("ReadParametersAndSaveToDatabaseShortAB | END");
        }

        #endregion READ-PARAMETERS-AND-SAVE-TO-DATABASE-AB

        #endregion READ-PARAMETERS-AND-SAVE-TO-DATABASE
    }
}
