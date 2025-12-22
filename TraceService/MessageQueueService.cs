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
        private readonly ILogger _logger;

        private readonly ConcurrentQueue<String> _messageQueue;
        private readonly SemaphoreSlim _signal;
        private readonly CancellationTokenSource _cts;

        public Boolean IsConnected { get; private set; }

        private PublisherSocket mqserver;

        public MessageQueueService()
        {
            IsConnected = false;
            _messageQueue = new ConcurrentQueue<String>();
            _signal = new SemaphoreSlim(0);
            _cts = new CancellationTokenSource();

            _logger = new LoggerConfiguration()
            .WriteTo.File(
                path: @"C:\Trace\netmqlogs_.txt",
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} | {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();
        }

        public void NetMQ_Start()
        {
            mqserver = new PublisherSocket();
            mqserver.Bind("tcp://*:5555");
        }

        public void NetMQ_Stop()
        {
            StopProcessingQueue();
            mqserver.Close();
            mqserver.Dispose();
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
            while (!_cts.Token.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cts.Token);

                if (_messageQueue.TryDequeue(out String message))
                {
                    try
                    {
                        mqserver.SendFrame(message);
                    }
                    catch (NetMQ.FaultException e)
                    {
                        LogEvent($"NetMQ SendFrame Fault Error | {e.Message}");
                    }
                    catch (Exception e)
                    {
                        LogEvent($"NetMQ SendFrame Error | {e.Message}");
                    }
                }
            }
        }

        public void StopProcessingQueue()
        {
            _cts.Cancel();
        }

        private void LogEvent(String message)
        {
            _logger.Information(message);
        }
    }
}
