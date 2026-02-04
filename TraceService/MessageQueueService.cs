using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NetMQ.Sockets;
using NetMQ;
using System.IO;
using Serilog;

namespace TraceService
{
    public class MessageQueueService
    {
        private readonly ConcurrentQueue<String> _messageQueue;
        private readonly SemaphoreSlim _signal;
        private readonly CancellationTokenSource _cts;
        private readonly ILogger _logger;

        private Task _processingTask;

        // Zmienna volatile, aby wątki widziały zmianę natychmiast
        private volatile bool _isRunning = false;
        public bool IsConnected => _isRunning;

        private PublisherSocket mqserver;

        public MessageQueueService()
        {
            _messageQueue = new ConcurrentQueue<String>();
            _signal = new SemaphoreSlim(0);
            _cts = new CancellationTokenSource();

            // Dedykowany plik logów dla NetMQ
            _logger = new LoggerConfiguration()
                .WriteTo.File(
                    path: @"C:\Trace\MQTT\netmqlogs_.txt",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 60,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} | {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();
        }

        public void NetMQ_Start()
        {
            if (_isRunning) return;

            try
            {
                // Inicjalizacja socketa
                mqserver = new PublisherSocket();

                // 1. ZABEZPIECZENIE PAMIĘCI: HighWatermark (żeby nie zapchać RAMu)
                mqserver.Options.SendHighWatermark = 1000;

                // 2. KLUCZOWE DLA UNIKNIĘCIA ZOMBIE: Linger = 0
                // To oznacza: "Przy zamykaniu nie czekaj ani milisekundy na wysłanie zaległych wiadomości. Ubijaj od razu."
                mqserver.Options.Linger = TimeSpan.Zero;

                mqserver.Bind("tcp://*:5555");
                _isRunning = true;
                _logger.Information("NetMQ Server Started on port 5555");

                // Uruchamiamy pętlę przetwarzania i przypisujemy ją do zmiennej
                _processingTask = Task.Run(() => ProcessingLoop());
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.Error($"NetMQ Start Error: {ex.Message}");
            }
        }

        public void NetMQ_Stop()
        {
            if (!_isRunning) return;

            try
            {
                _logger.Information("NetMQ Server stopping...");

                // 1. Najpierw wysyłamy sygnał STOP do pętli
                _isRunning = false; // Logiczna flaga
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }

                // 2. Czekamy aż pętla zakończy pracę 
                // Dajemy jej np. 2 sekundy na wyjście z WaitAsync i zakończenie.
                if (_processingTask != null)
                {
                    // Dajemy mu 2 sekundy na wyjście. Jak nie, to trudno.
                    bool terminated = _processingTask.Wait(TimeSpan.FromSeconds(2));
                    if (!terminated)
                    {
                        _logger.Warning("NetMQ Processing Task did not finish in time.");
                    }
                }

                // 3. Teraz, gdy nikt nie używa socketa, możemy go bezpiecznie zamknąć
                if (mqserver != null)
                {
                    if (!mqserver.IsDisposed)
                    {
                        mqserver.Unbind("tcp://*:5555");
                        mqserver.Close();
                        mqserver.Dispose();
                    }
                    mqserver = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"NetMQ Stop Error: {ex.Message}");
            }
            finally
            {
                _logger.Information("NetMQ Server Stopped cleanly");
            }
        }

        public void EnqueueMessage(String message)
        {
            // Zabezpieczenie: nie dodajemy do kolejki, jeśli serwis się zatrzymuje
            if(!_isRunning || _cts.IsCancellationRequested) return;

            _messageQueue.Enqueue(message);

            // Bezpieczne zwolnienie semafora
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException) { }
        }

        private async Task ProcessingLoop()
        {
            _logger.Information("Message Queue Processing Loop Started");

            while (_isRunning && !_cts.IsCancellationRequested)
            {
                try
                {
                    // Czekamy na sygnał LUB na anulowanie tokena
                    await _signal.WaitAsync(_cts.Token);

                    if (_messageQueue.TryDequeue(out String message))
                    {
                        // Dodatkowe sprawdzenie przed wysłaniem
                        if (mqserver != null && !mqserver.IsDisposed)
                        {
                            // TrySendFrame jest bezpieczniejsze niż SendFrame przy zamykaniu
                            mqserver.TrySendFrame(message);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // To jest normalne wyjście z pętli przy zamykaniu
                    break;
                }
                catch (NetMQ.FaultException e)
                {
                    _logger.Error($"NetMQ Fault: {e.Message}");
                }
                catch (Exception e)
                {
                    _logger.Error($"NetMQ Processing Error: {e.Message}");
                }
            }

            _logger.Information("Processing Loop Ended");
        }
    }
}
