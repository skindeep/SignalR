﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SignalR.Infrastructure;

namespace SignalR
{
    public class Connection : IConnection, ITransportConnection
    {
        private readonly IMessageBus _messageBus;
        private readonly IJsonSerializer _serializer;
        private readonly string _baseSignal;
        private readonly string _connectionId;
        private readonly HashSet<string> _signals;
        private readonly SafeSet<string> _groups;
        private readonly ITraceManager _trace;
        private readonly IPerformanceCounterWriter _counters;
        private bool _disconnected;
        private bool _aborted;

        public Connection(IMessageBus messageBus,
                          IJsonSerializer jsonSerializer,
                          string baseSignal,
                          string connectionId,
                          IEnumerable<string> signals,
                          IEnumerable<string> groups,
                          ITraceManager traceManager,
                          IPerformanceCounterWriter performanceCounterWriter)
        {
            _messageBus = messageBus;
            _serializer = jsonSerializer;
            _baseSignal = baseSignal;
            _connectionId = connectionId;
            _signals = new HashSet<string>(signals);
            _groups = new SafeSet<string>(groups);
            _trace = traceManager;
            _counters = performanceCounterWriter;
        }

        private IEnumerable<string> Signals
        {
            get
            {
                return _signals.Concat(_groups.GetSnapshot());
            }
        }

        private TraceSource Trace
        {
            get
            {
                return _trace["SignalR.Connection"];
            }
        }

        public virtual Task Broadcast(object value)
        {
            return Send(_baseSignal, value);
        }

        public virtual Task Send(string signal, object value)
        {
            return SendMessage(signal, value);
        }

        public Task<PersistentResponse> ReceiveAsync(CancellationToken timeoutToken)
        {
            Trace.TraceInformation("Waiting for new messages");

            return _messageBus.GetMessages(Signals, null, timeoutToken)
                              .Then(result => GetResponse(result));
        }

        public Task<PersistentResponse> ReceiveAsync(string messageId, CancellationToken timeoutToken)
        {
            Trace.TraceInformation("Waiting for messages from {0}.", messageId);

            return _messageBus.GetMessages(Signals, messageId, timeoutToken)
                              .Then(result => GetResponse(result));
        }

        public Task SendCommand(SignalCommand command)
        {
            return SendMessage(SignalCommand.AddCommandSuffix(_connectionId), command);
        }

        private PersistentResponse GetResponse(MessageResult result)
        {
            // Do a single sweep through the results to process commands and extract values
            var messageValues = ProcessResults(result.Messages);

            var response = new PersistentResponse
            {
                MessageId = result.LastMessageId,
                Messages = messageValues,
                Disconnect = _disconnected,
                Aborted = _aborted
            };

            PopulateResponseState(response);

            Trace.TraceInformation("Connection '{0}' received {1} messages, last id {2}", _connectionId, result.Messages.Count, result.LastMessageId);

            _counters.IncrementBy(PerformanceCounters.ConnectionMessagesReceived, result.Messages.Count);
            _counters.IncrementBy(PerformanceCounters.ConnectionMessagesReceivedPerSec, result.Messages.Count);

            return response;
        }

        private List<object> ProcessResults(IList<Message> source)
        {
            var messageValues = new List<object>();
            foreach (var message in source)
            {
                if (SignalCommand.IsCommand(message))
                {
                    var command = WrappedValue.Unwrap<SignalCommand>(message.Value, _serializer);
                    ProcessCommand(command);
                }
                else
                {
                    messageValues.Add(WrappedValue.Unwrap(message.Value, _serializer));
                }
            }
            return messageValues;
        }

        private void ProcessCommand(SignalCommand command)
        {
            switch (command.Type)
            {
                case CommandType.AddToGroup:
                    _groups.Add((string)command.Value);
                    break;
                case CommandType.RemoveFromGroup:
                    _groups.Remove((string)command.Value);
                    break;
                case CommandType.Disconnect:
                    _disconnected = true;
                    break;
                case CommandType.Abort:
                    _aborted = true;
                    break;
            }
        }

        private Task SendMessage(string key, object value)
        {
            TraceSend(key, value);

            _counters.Increment(PerformanceCounters.ConnectionMessagesSent);
            _counters.Increment(PerformanceCounters.ConnectionMessagesSentPerSecond);

            var wrappedValue = new WrappedValue(value, _serializer);
            return _messageBus.Send(_connectionId, key, wrappedValue).Catch();
        }

        private void PopulateResponseState(PersistentResponse response)
        {
            // Set the groups on the outgoing transport data
            if (_groups.Count > 0)
            {
                response.TransportData["Groups"] = _groups.GetSnapshot();
            }
        }

        private void TraceSend(string key, object value)
        {
            var command = value as SignalCommand;
            if (command != null)
            {
                if (command.Type == CommandType.AddToGroup)
                {
                    Trace.TraceInformation("Sending Add to group '{0}'.", command.Value);
                }
                else if (command.Type == CommandType.RemoveFromGroup)
                {
                    Trace.TraceInformation("Sending Remove from group '{0}'.", command.Value);
                }
                else if (command.Type == CommandType.Abort)
                {
                    Trace.TraceInformation("Sending Aborted command '{0}'", _connectionId);
                }
                else
                {
                    Trace.TraceInformation("Sending message to '{0}'", key);
                }
            }
            else
            {
                Trace.TraceInformation("Sending message to '{0}'", key);
            }
        }
    }
}