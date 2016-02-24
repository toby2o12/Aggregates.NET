﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NServiceBus;
using NServiceBus.ObjectBuilder;
using NServiceBus.Unicast;
using NServiceBus.Settings;
using NServiceBus.Logging;
using Aggregates.Exceptions;
using System.Threading.Tasks.Dataflow;
using Microsoft.Practices.TransientFaultHandling;
using Aggregates.Attributes;
using NServiceBus.MessageInterfaces;
using System.Collections.Concurrent;
using Metrics;
using Aggregates.Contracts;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Threading;
using Aggregates.Extensions;

namespace Aggregates.Internal
{
    public class NServiceBusDispatcher : IDispatcher
    {
        private class ParellelJob
        {
            public Object Handler { get; set; }
            public Object Event { get; set; }
        }
        private class Job
        {
            public Object Event { get; set; }
            public IEventDescriptor Descriptor { get; set; }
        }

        private static readonly ILog Logger = LogManager.GetLogger(typeof(NServiceBusDispatcher));
        private readonly IBus _bus;
        private readonly IBuilder _builder;
        private readonly IMessageCreator _eventFactory;
        private readonly IMessageMapper _mapper;
        private readonly IMessageHandlerRegistry _handlerRegistry;

        private readonly IDictionary<Type, IDictionary<Type, Boolean>> _parallelCache;
        private readonly IDictionary<Type, Boolean> _eventParallelCache;
        private readonly ConcurrentDictionary<String, IList<Type>> _invokeCache;
        private readonly ActionBlock<Job> _queue;
        private readonly ExecutionDataflowBlockOptions _parallelOptions;
        private readonly JsonSerializerSettings _jsonSettings;

        private static DateTime Stamp = DateTime.UtcNow;

        private Meter _eventsMeter = Metric.Meter("Events", Unit.Events);
        private Metrics.Timer _eventsTimer = Metric.Timer("Event Duration", Unit.Events);

        private Meter _errorsMeter = Metric.Meter("Event Errors", Unit.Errors);

        public NServiceBusDispatcher(IBus bus, IBuilder builder, ReadOnlySettings settings, JsonSerializerSettings jsonSettings)
        {
            _bus = bus;
            _builder = builder;
            _eventFactory = builder.Build<IMessageCreator>();
            _mapper = builder.Build<IMessageMapper>();
            _handlerRegistry = builder.Build<IMessageHandlerRegistry>();
            _jsonSettings = jsonSettings;

            _parallelCache = new Dictionary<Type, IDictionary<Type, Boolean>>();
            _eventParallelCache = new Dictionary<Type, Boolean>();
            _invokeCache = new ConcurrentDictionary<String, IList<Type>>();

            var parallelism = settings.Get<Int32?>("SetEventStoreMaxDegreeOfParallelism") ?? Environment.ProcessorCount;
            var capacity = settings.Get<Int32?>("SetEventStoreCapacity") ?? 10000;

            _parallelOptions = new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = parallelism,
                BoundedCapacity = capacity,
            };
            _queue = new ActionBlock<Job>((x) => Process(x.Event, x.Descriptor), _parallelOptions);

        }

        private void Process(Object @event, IEventDescriptor descriptor)
        {
            //Logger.InfoFormat("Time since last process: {0} ms", (DateTime.UtcNow - Stamp).TotalMilliseconds);
            //Stamp = DateTime.UtcNow;

            Thread.CurrentThread.Rename("Dispatcher");
            _eventsMeter.Mark();

            var eventType = _mapper.GetMappedTypeFor(@event.GetType());

            // Use NSB internal handler registry to directly call Handle(@event)
            // This will prevent the event from being queued on MSMQ

            Exception lastException = null;
            var retries = 0;
            bool success = false;
            do
            {
                Logger.DebugFormat("Processing event {0}", eventType.FullName);


                retries++;
                var handlersToInvoke = _invokeCache.GetOrAdd(eventType.FullName,
                    (key) => _handlerRegistry.GetHandlerTypes(eventType).ToList());

                using (var childBuilder = _builder.CreateChildBuilder())
                {
                    var uows = childBuilder.BuildAll<IConsumerUnitOfWork>();
                    var mutators = childBuilder.BuildAll<IEventMutator>();

                    try
                    {
                        using (_eventsTimer.NewContext())
                        {
                            if (mutators != null && mutators.Any())
                                foreach (var mutate in mutators)
                                {
                                    Logger.DebugFormat("Mutating incoming event {0} with mutator {1}", eventType.FullName, mutate.GetType().FullName);
                                    @event = mutate.MutateIncoming(@event, descriptor);
                                }

                            if (uows != null && uows.Any())
                                foreach (var uow in uows)
                                {
                                    uow.Builder = childBuilder;
                                    uow.Begin();
                                }

                            //var parallelQueue = new ActionBlock<ParellelJob>(job => ExecuteJob(job), _parallelOptions);
                            foreach (var handler in handlersToInvoke)
                            {
                                var parellelJob = new ParellelJob
                                {
                                    Handler = childBuilder.Build(handler),
                                    Event = @event
                                };

                                //IDictionary<Type, Boolean> cached;
                                //Boolean parallel;
                                //if (!_parallelCache.TryGetValue(handler, out cached))
                                //{
                                //    cached = new Dictionary<Type, bool>();
                                //    _parallelCache[handler] = cached;
                                //}
                                //if (!cached.TryGetValue(eventType, out parallel))
                                //{

                                //    var interfaceType = typeof(IHandleMessages<>).MakeGenericType(eventType);

                                //    if (!interfaceType.IsAssignableFrom(handler))
                                //        continue;
                                //    var methodInfo = handler.GetInterfaceMap(interfaceType).TargetMethods.FirstOrDefault();
                                //    if (methodInfo == null)
                                //        continue;

                                //    parallel = handler.GetCustomAttributes(typeof(ParallelAttribute), false).Any() || methodInfo.GetCustomAttributes(typeof(ParallelAttribute), false).Any();
                                //    _parallelCache[handler][eventType] = parallel;
                                //}

                                // If parallel - put on the threaded execution queue
                                // Post returns false if its full - so keep retrying until it gets in
                                //if (parallel)
                                //    await parallelQueue.SendAsync(parellelJob);
                                //else
                                //ExecuteJob(parellelJob);
                                var handlerRetries = 0;
                                var handlerSuccess = false;
                                do
                                {
                                    try
                                    {
                                        var instance = childBuilder.Build(handler);
                                        handlerRetries++;
                                        _handlerRegistry.InvokeHandle(instance, @event);
                                        handlerSuccess = true;
                                    }
                                    catch (RetryException e)
                                    {
                                        Logger.InfoFormat("Received retry signal while dispatching event {0} to {1}. Retry: {2}\nException: {3}", eventType.FullName, handler.FullName, handlerRetries, e);
                                    }

                                } while (!handlerSuccess && handlerRetries <= 3);

                                if (!handlerSuccess)
                                {
                                    Logger.ErrorFormat("Failed executing event {0} on handler {1}", eventType.FullName, handler.FullName);
                                    throw new RetryException(String.Format("Failed executing event {0} on handler {1}", eventType.FullName, handler.FullName));
                                }
                            }

                            //parallelQueue.Complete();
                            //await parallelQueue.Completion;
                        }


                        if (uows != null && uows.Any())
                            foreach (var uow in uows)
                                uow.End();

                        success = true;
                    }
                    catch (Exception ex)
                    {

                        if (uows != null && uows.Any())
                            foreach (var uow in uows)
                                uow.End(ex);

                        lastException = ex;
                        Thread.Sleep(50);
                    }
                }
            } while (!success && retries <= 3);
            if (!success)
            {
                _errorsMeter.Mark();
                Logger.ErrorFormat("Failed to process event {0}.  Payload: \n{1}\n Exception: {2}", @event.GetType().FullName, JsonConvert.SerializeObject(@event, _jsonSettings), lastException);
            }
        }
        

        public void Dispatch(Object @event, IEventDescriptor descriptor = null)
        {
            Logger.DebugFormat("Queueing event {0} for processing", @event.GetType().FullName);
            _queue.SendAsync(new Job { Event = @event, Descriptor = descriptor }).Wait();
        }

        public void Dispatch<TEvent>(Action<TEvent> action)
        {
            var @event = _eventFactory.CreateInstance(action);
            this.Dispatch(@event);
        }
    }
}