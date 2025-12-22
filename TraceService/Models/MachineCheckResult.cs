using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TraceService.Models
{
    public class MachineCheckResult
    {
        public int Result { get; set; }
        public DateTime Date { get; set; }
        public bool IsFound { get; set; }
        public bool IsOld { get; set; }
        public bool IsScrap => Result == 4;
    }
}
