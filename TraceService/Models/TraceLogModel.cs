using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TraceService.Models
{
    // Uniwersalny model reprezentujący jeden wpis w tabeli dbo.logs
    public class TraceLogModel
    {
        public int MachineId { get; set; }
        public string DmcCode1 { get; set; }
        public string DmcCode2 { get; set; }
        public int OperationResult1 { get; set; }
        public int OperationResult2 { get; set; }
        public DateTime OperationDateTime1 { get; set; }
        public DateTime OperationDateTime2 { get; set; }
        public string Reference { get; set; }
        public int CycleTime { get; set; }
        public string Operator { get; set; }

        // Tablice danych - zakładamy maksymalne rozmiary z wersji "Long"
        public int[] Ints { get; set; } = new int[10];
        public float[] Reals { get; set; } = new float[100];
        public DateTime[] Dtls { get; set; } = new DateTime[5];
        public string[] Strings { get; set; } = new string[7]; // Max z wersji Short to 7, Long to 5
    }
}
