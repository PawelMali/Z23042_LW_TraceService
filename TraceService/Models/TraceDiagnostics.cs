using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TraceService.Enums;

namespace TraceService.Models
{
    public class TraceDiagnostics : IDisposable
    {
        private readonly ILogger _logger;
        private readonly System.Diagnostics.Stopwatch _swTotal;
        private readonly System.Text.StringBuilder _sbDetails;
        private readonly string _contextInfo;

        // Wynik operacji (do logu)
        private short _finalPartStatus = 0;
        private TraceErrorStatus _finalErrorStatus = TraceErrorStatus.Ok;
        private bool _resultSet = false;

        public TraceDiagnostics(ILogger logger, string machineId, string dmc1, string dmc2)
        {
            _logger = logger;
            _contextInfo = $"Logic Check | ID: {machineId} | DMCs: '{dmc1}', '{dmc2}'";
            _swTotal = System.Diagnostics.Stopwatch.StartNew();
            _sbDetails = new System.Text.StringBuilder();
        }

        // Metoda wykonująca akcję i mierząca jej czas (generyczna)
        public T Measure<T>(string stepName, Func<T> action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                sw.Stop();
                _sbDetails.Append($"[{stepName}: {sw.ElapsedMilliseconds}ms] ");
            }
        }

        // Ustawienie wyniku końcowego (zwraca tuple, żeby łatwo użyć w return)
        public (short Part, TraceErrorStatus Error) SetResult(short part, TraceErrorStatus error)
        {
            _finalPartStatus = part;
            _finalErrorStatus = error;
            _resultSet = true;
            return (part, error);
        }

        public void Dispose()
        {
            _swTotal.Stop();

            string resultStr = _resultSet
                ? $"Result: Status={_finalPartStatus}, Error={_finalErrorStatus} ({(int)_finalErrorStatus})"
                : "Result: EXCEPTION/UNKNOWN";

            // Jeden wpis w logu
            _logger.Information($"{_contextInfo} | {resultStr} | Total: {_swTotal.ElapsedMilliseconds}ms | Steps: {_sbDetails}");
        }
    }
}
