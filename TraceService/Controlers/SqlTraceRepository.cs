using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TraceService.Interfaces;
using TraceService.Models;

namespace TraceService.Controlers
{
    public class SqlTraceRepository : ITraceRepository
    {
        private readonly string _connectionString;

        public SqlTraceRepository(string server, string port, string database, string user, string password)
        {
            _connectionString = $"Data source={server},{port};Initial Catalog={database};User ID={user};Password={password};";
        }

        public void SaveLog(TraceLogModel log)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (SqlCommand cmd = connection.CreateCommand())
                {
                    // Budowanie zapytania SQL (skrócona wersja dla czytelności - w rzeczywistości tu będzie pełny INSERT)
                    // Dla optymalizacji generujemy parametry w pętli
                    string columns = "machine_id, dmc_code1, dmc_code2, operation_result1, operation_result2, operation_datetime1, operation_datetime2, reference, cycle_time, operator";
                    string values = "@machineID, @dmcCode1, @dmcCode2, @operationResult1, @operationResult2, @operationDatetime1, @operationDatetime2, @reference, @cycleTime, @operator";

                    // Dodawanie dynamicznych kolumn (int_1...int_10, real_1...real_100 itd.)
                    AddDynamicColumnsSql(ref columns, ref values, "int_", 1, 10);
                    AddDynamicColumnsSql(ref columns, ref values, "real_", 1, 100);
                    AddDynamicColumnsSql(ref columns, ref values, "dtl_", 1, 5);
                    // Uwaga: Stringi mają różne limity w Long/Short, zakładamy sumę (do 7)
                    AddDynamicColumnsSql(ref columns, ref values, "string_", 1, 7);

                    cmd.CommandText = $"INSERT INTO dbo.logs ({columns}) VALUES ({values})";

                    // Parametry podstawowe
                    cmd.Parameters.Add("@machineID", SqlDbType.Int).Value = log.MachineId;
                    cmd.Parameters.Add("@dmcCode1", SqlDbType.VarChar, 256).Value = log.DmcCode1 ?? "";
                    cmd.Parameters.Add("@dmcCode2", SqlDbType.VarChar, 256).Value = log.DmcCode2 ?? "";
                    cmd.Parameters.Add("@operationResult1", SqlDbType.Int).Value = log.OperationResult1;
                    cmd.Parameters.Add("@operationResult2", SqlDbType.Int).Value = log.OperationResult2;
                    cmd.Parameters.Add("@operationDatetime1", SqlDbType.DateTime).Value = log.OperationDateTime1;
                    cmd.Parameters.Add("@operationDatetime2", SqlDbType.DateTime).Value = log.OperationDateTime2;
                    cmd.Parameters.Add("@reference", SqlDbType.VarChar, 256).Value = log.Reference ?? "";
                    cmd.Parameters.Add("@cycleTime", SqlDbType.Int).Value = log.CycleTime;
                    cmd.Parameters.Add("@operator", SqlDbType.VarChar, 256).Value = log.Operator ?? "";

                    // Parametry tablicowe - pętle zamiast 100 linii kodu!
                    AddArrayParameters(cmd, "@int", SqlDbType.Int, log.Ints, 10);
                    AddArrayParameters(cmd, "@real", SqlDbType.Real, log.Reals, 100);
                    AddArrayParameters(cmd, "@dtl", SqlDbType.DateTime, log.Dtls, 5);
                    AddArrayParameters(cmd, "@string", SqlDbType.VarChar, log.Strings, 7, 256);

                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool IsScrapDetected(string dmc1, string dmc2)
        {
            // Szybkie sprawdzenie czy kiedykolwiek wystąpił krytyczny błąd SCRAP (4)
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TOP(1) 1 FROM dbo.logs WHERE ((dmc_code1 = @p1 AND LEN(dmc_code1) > 0) OR (dmc_code2 = @p2 AND LEN(dmc_code2) > 0)) AND (operation_result1 = 4 OR operation_result2 = 4)";
                    cmd.Parameters.Add("@p1", SqlDbType.VarChar, 256).Value = dmc1 ?? "";
                    cmd.Parameters.Add("@p2", SqlDbType.VarChar, 256).Value = dmc2 ?? "";
            
                    using (var reader = cmd.ExecuteReader())
                    {
                        return reader.Read();
                    }
                }
            }
        }

        public bool IsDmcProcessed(int machineId, string dmc1, string dmc2)
        {
            // Implementacja logiki CheckOnlyOnce
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (SqlCommand cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT TOP(1) machine_id FROM dbo.logs WHERE machine_id = @mId AND ((dmc_code1 = @p1 AND LEN(dmc_code1) > 0) OR (dmc_code2 = @p2 AND LEN(dmc_code2) > 0)) ORDER BY id DESC";
                    cmd.Parameters.Add("@mId", SqlDbType.Int).Value = machineId;
                    cmd.Parameters.Add("@p1", SqlDbType.VarChar, 256).Value = dmc1 ?? "";
                    cmd.Parameters.Add("@p2", SqlDbType.VarChar, 256).Value = dmc2 ?? "";

                    using (var reader = cmd.ExecuteReader())
                    {
                        return reader.Read();
                    }
                }
            }
        }

        public RemoteMachineConfig GetRemoteMachineConfig(int machineId)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT lines.database_ip, lines.database_login, lines.database_password 
                                FROM dbo.machines 
                                LEFT JOIN dbo.lines ON lines.id = machines.id_line 
                                WHERE machines.machine_id = @mId";
                    cmd.Parameters.Add("@mId", SqlDbType.Int).Value = machineId;

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new RemoteMachineConfig
                            {
                                Ip = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim(),
                                User = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
                                Password = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim()
                            };
                        }
                    }
                }
            }
            return null;
        }

        public (int? Result, DateTime? Timestamp) GetLatestLogEntry(int machineId, string dmc1, string dmc2, bool checkSecondary)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    // Budujemy warunek DMC
                    string dmcCondition = checkSecondary
                        ? "(dmc_code1 = @p1 OR dmc_code2 = @p2)"
                        : "dmc_code1 = @p1";

                    // UWAGA: Usunęliśmy warunek daty z SQL. Pobieramy po prostu ostatni wpis.
                    cmd.CommandText = $@"SELECT TOP(1) operation_result1, operation_result2, operation_datetime2 
                                 FROM dbo.logs 
                                 WHERE machine_id = @mId AND {dmcCondition} 
                                 ORDER BY id DESC"; // Zawsze bierzemy najnowszy

                    cmd.Parameters.Add("@mId", SqlDbType.Int).Value = machineId;
                    cmd.Parameters.Add("@p1", SqlDbType.VarChar, 256).Value = dmc1 ?? "";
                    if (checkSecondary)
                        cmd.Parameters.Add("@p2", SqlDbType.VarChar, 256).Value = dmc2 ?? "";

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            int res1 = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                            int res2 = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                            DateTime date = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2);

                            // Agregacja wyniku (zgodnie z logiką: 2 jest ważniejsze niż 1, ale bierzemy z operacji 2 jeśli jest nowsza/ważniejsza)
                            // Zakładam prostą logikę: jeśli gdziekolwiek jest 2 to NOK, jeśli nie to bierzemy result2 chyba że pusty.
                            // (Tu zachowujemy oryginalną logikę wyboru wyniku)
                            int finalResult = res1;
                            if (res1 == 2 || res2 == 2) finalResult = 2;
                            else if (res2 > res1) finalResult = res2; // Np. res1=1 (start), res2=1 (koniec) -> 1

                            return (finalResult, date);
                        }
                    }
                }
            }
            // Zwracamy null, jeśli nie znaleziono żadnego wpisu
            return (null, null);
        }

        // --- Helpery ---

        private void AddDynamicColumnsSql(ref string cols, ref string vals, string prefix, int start, int count)
        {
            for (int i = start; i <= count; i++)
            {
                cols += $", {prefix}{i}";
                vals += $", @{prefix.Replace("_", "")}{i}"; // np. @real1
            }
        }

        private void AddArrayParameters<T>(SqlCommand cmd, string prefix, SqlDbType type, T[] data, int count, int size = 0)
        {
            // Minimalna data akceptowana przez SQL Server (typ DATETIME)
            DateTime sqlMinDate = new DateTime(1753, 1, 1);

            for (int i = 0; i < count; i++)
            {
                var paramName = $"{prefix}{i + 1}";
                object val = DBNull.Value;

                if (data != null && i < data.Length)
                {
                    val = data[i];
                    if (val is DateTime dt)
                    {
                        // Jeśli data jest mniejsza niż rok 1753 (np. 0001-01-01), wysyłamy NULL
                        if (dt < sqlMinDate)
                        {
                            val = DBNull.Value;
                        }
                    }
                }

                var p = cmd.Parameters.Add(paramName, type);
                if (size > 0) p.Size = size;
                p.Value = val ?? DBNull.Value;
            }
        }
    }
}
