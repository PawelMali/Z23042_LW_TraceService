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

        public Boolean IsConnected { get; private set; }

        private PublisherSocket mqserver;

public MessageQueueService()
        {
            IsConnected = false;
            _messageQueue = new ConcurrentQueue<String>();
            _signal = new SemaphoreSlim(0);
            _cts = new CancellationTokenSource();

            // Dedykowany plik logów dla NetMQ
            _logger = new LoggerConfiguration()
                .WriteTo.File(
                    path: @"C:\Trace\MQTT\netmqlogs_.txt",
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} | {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();
        }

        public void NetMQ_Start()
        {
            try
            {
                mqserver = new PublisherSocket();
                mqserver.Bind("tcp://*:5555");
                _logger.Information("NetMQ Server Started on port 5555");
            }
            catch (Exception ex)
            {
                _logger.Error($"NetMQ Start Error: {ex.Message}");
            }
        }

        public void NetMQ_Stop()
        {
            try
            {
                StopProcessingQueue();
                if (mqserver != null)
                {
                    mqserver.Close();
                    mqserver.Dispose();
                }
                NetMQConfig.Cleanup();
                _logger.Information("NetMQ Server Stopped");
            }
            catch (Exception ex)
            {
                _logger.Error($"NetMQ Stop Error: {ex.Message}");
            }
            NetMQConfig.Cleanup();
        }

        public void EnqueueMessage(String message)
        {
            _messageQueue.Enqueue(message);
            _signal.Release();
        }

        public async Task StartProcessingQueue()
        {
            IsConnected = true;
            _logger.Information("Message Queue Processing Started");

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_cts.Token);

                    if (_messageQueue.TryDequeue(out String message))
                    {
                        mqserver.SendFrame(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (NetMQ.FaultException e)
                {
                    _logger.Error($"NetMQ SendFrame Fault: {e.Message}");
                }
                catch (Exception e)
                {
                    _logger.Error($"NetMQ Processing Error: {e.Message}");
                }
            }
            IsConnected = false;
        }

        public void StopProcessingQueue()
        {
            _cts.Cancel();
        }
    }
}
