// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Internal
{
    internal sealed class Host : IHost, IAsyncDisposable
    {
        private readonly ILogger<Host> _logger;
        private readonly IHostLifetime _hostLifetime;
        private readonly ApplicationLifetime _applicationLifetime;
        private readonly HostOptions _options;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly PhysicalFileProvider _defaultProvider;
        private IEnumerable<IHostedService>? _hostedServices;
        private IEnumerable<IHostedLifecycleService>? _hostedLifecycleServices;
        private volatile bool _stopCalled;

        public Host(IServiceProvider services,
                    IHostEnvironment hostEnvironment,
                    PhysicalFileProvider defaultProvider,
                    IHostApplicationLifetime applicationLifetime,
                    ILogger<Host> logger,
                    IHostLifetime hostLifetime,
                    IOptions<HostOptions> options)
        {
            ThrowHelper.ThrowIfNull(services);
            ThrowHelper.ThrowIfNull(applicationLifetime);
            ThrowHelper.ThrowIfNull(logger);
            ThrowHelper.ThrowIfNull(hostLifetime);

            Services = services;
            _applicationLifetime = (applicationLifetime as ApplicationLifetime)!;
            _hostEnvironment = hostEnvironment;
            _defaultProvider = defaultProvider;

            if (_applicationLifetime is null)
            {
                throw new ArgumentException(SR.IHostApplicationLifetimeReplacementNotSupported, nameof(applicationLifetime));
            }
            _logger = logger;
            _hostLifetime = hostLifetime;
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public IServiceProvider Services { get; }

        /// <summary>
        /// Order:
        ///  IHostLifetime.WaitForStartAsync
        ///  IHostedLifecycleService.StartingAsync
        ///  IHostedService.Start
        ///  IHostedLifecycleService.StartedAsync
        ///  IHostApplicationLifetime.ApplicationStarted
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _logger.Starting();

            CancellationTokenSource? cts = null;
            CancellationTokenSource linkedCts;
            if (_options.StartupTimeout != Timeout.InfiniteTimeSpan)
            {
                cts = new CancellationTokenSource(_options.StartupTimeout);
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken, _applicationLifetime.ApplicationStopping);
            }
            else
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _applicationLifetime.ApplicationStopping);
            }

            using (cts)
            using (linkedCts)
            {
                CancellationToken token = linkedCts.Token;

                // This may not catch exceptions.
                await _hostLifetime.WaitForStartAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                List<Exception> exceptions = new();
                _hostedServices = Services.GetRequiredService<IEnumerable<IHostedService>>();
                _hostedLifecycleServices = GetHostLifecycles(_hostedServices);
                bool concurrent = _options.ServicesStartConcurrently;
                bool abortOnFirstException = !concurrent;

                if (_hostedLifecycleServices is not null)
                {
                    // Call StartingAsync().
                    await ForeachService(_hostedLifecycleServices, token, concurrent, abortOnFirstException, exceptions,
                        (service, token) => service.StartingAsync(token)).ConfigureAwait(false);
                }

                // Call StartAsync().
                await ForeachService(_hostedServices, token, concurrent, abortOnFirstException, exceptions,
                    async (service, token) =>
                    {
                        await service.StartAsync(token).ConfigureAwait(false);

                        if (service is BackgroundService backgroundService)
                        {
                            _ = TryExecuteBackgroundServiceAsync(backgroundService);
                        }
                    }).ConfigureAwait(false);

                if (_hostedLifecycleServices is not null)
                {
                    // Call StartedAsync().
                    await ForeachService(_hostedLifecycleServices, token, concurrent, abortOnFirstException, exceptions,
                        (service, token) => service.StartedAsync(token)).ConfigureAwait(false);
                }

                if (exceptions.Count > 0)
                {
                    if (exceptions.Count == 1)
                    {
                        // Rethrow if it's a single error
                        Exception singleException = exceptions[0];
                        _logger.HostedServiceStartupFaulted(singleException);
                        ExceptionDispatchInfo.Capture(singleException).Throw();
                    }
                    else
                    {
                        var ex = new AggregateException("One or more hosted services failed to start.", exceptions);
                        _logger.HostedServiceStartupFaulted(ex);
                        throw ex;
                    }
                }

                // Call IHostApplicationLifetime.Started
                // This catches all exceptions and does not re-throw.
                _applicationLifetime.NotifyStarted();
            }

            _logger.Started();
        }

        private async Task TryExecuteBackgroundServiceAsync(BackgroundService backgroundService)
        {
            // backgroundService.ExecuteTask may not be set (e.g. if the derived class doesn't call base.StartAsync)
            Task? backgroundTask = backgroundService.ExecuteTask;
            if (backgroundTask is null)
            {
                return;
            }

            try
            {
                await backgroundTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // When the host is being stopped, it cancels the background services.
                // This isn't an error condition, so don't log it as an error.
                if (_stopCalled && backgroundTask.IsCanceled && ex is OperationCanceledException)
                {
                    return;
                }

                _logger.BackgroundServiceFaulted(ex);
                if (_options.BackgroundServiceExceptionBehavior == BackgroundServiceExceptionBehavior.StopHost)
                {
                    _logger.BackgroundServiceStoppingHost(ex);

                    // This catches all exceptions and does not re-throw.
                    _applicationLifetime.StopApplication();
                }
            }
        }

        /// <summary>
        /// Order:
        ///  IHostedLifecycleService.StoppingAsync
        ///  IHostApplicationLifetime.ApplicationStopping
        ///  IHostedService.Stop
        ///  IHostedLifecycleService.StoppedAsync
        ///  IHostApplicationLifetime.ApplicationStopped
        ///  IHostLifetime.StopAsync
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            _stopCalled = true;
            _logger.Stopping();

            CancellationTokenSource? cts = null;
            CancellationTokenSource linkedCts;
            if (_options.ShutdownTimeout != Timeout.InfiniteTimeSpan)
            {
                cts = new CancellationTokenSource(_options.ShutdownTimeout);
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            }
            else
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            using (cts)
            using (linkedCts)
            {
                CancellationToken token = linkedCts.Token;

                List<Exception> exceptions = new();
                if (_hostedServices is null) // Started?
                {

                    // Call IHostApplicationLifetime.ApplicationStopping.
                    // This catches all exceptions and does not re-throw.
                    _applicationLifetime.StopApplication();
                }
                else
                {
                    // Ensure hosted services are stopped in LIFO order
                    IEnumerable<IHostedService> reversedServices = _hostedServices.Reverse();
                    IEnumerable<IHostedLifecycleService>? reversedLifetimeServices = _hostedLifecycleServices?.Reverse();
                    bool concurrent = _options.ServicesStopConcurrently;

                    // Call StoppingAsync().
                    if (reversedLifetimeServices is not null)
                    {
                        await ForeachService(reversedLifetimeServices, token, concurrent, abortOnFirstException: false, exceptions,
                            (service, token) => service.StoppingAsync(token)).ConfigureAwait(false);
                    }

                    // Call IHostApplicationLifetime.ApplicationStopping.
                    // This catches all exceptions and does not re-throw.
                    _applicationLifetime.StopApplication();

                    // Call StopAsync().
                    await ForeachService(reversedServices, token, concurrent, abortOnFirstException: false, exceptions, (service, token) =>
                        service.StopAsync(token)).ConfigureAwait(false);

                    if (reversedLifetimeServices is not null)
                    {
                        // Call StoppedAsync().
                        await ForeachService(reversedLifetimeServices, token, concurrent, abortOnFirstException: false, exceptions, (service, token) =>
                            service.StoppedAsync(token)).ConfigureAwait(false);
                    }
                }

                // Call IHostApplicationLifetime.Stopped
                // This catches all exceptions and does not re-throw.
                _applicationLifetime.NotifyStopped();

                // This may not catch exceptions, so we do it here.
                try
                {
                    await _hostLifetime.StopAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }

                if (exceptions.Count > 0)
                {
                    if (exceptions.Count == 1)
                    {
                        // Rethrow if it's a single error
                        Exception singleException = exceptions[0];
                        _logger.StoppedWithException(singleException);
                        ExceptionDispatchInfo.Capture(singleException).Throw();
                    }
                    else
                    {
                        var ex = new AggregateException("One or more hosted services failed to stop.", exceptions);
                        _logger.StoppedWithException(ex);
                        throw ex;
                    }
                }
            }

            _logger.Stopped();
        }

        private static async Task ForeachService<T>(
            IEnumerable<T> services,
            CancellationToken token,
            bool concurrent,
            bool abortOnFirstException,
            List<Exception> exceptions,
            Func<T, CancellationToken, Task> operation)
        {
            if (concurrent)
            {
                // The beginning synchronous portions of the implementations are run serially in registration order for
                // performance since it is common to return Task.Completed as a noop.
                // Any subsequent asynchronous portions are grouped together run concurrently.
                List<Task>? tasks = null;

                foreach (T service in services)
                {
                    Task task;
                    try
                    {
                        task = operation(service, token);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex); // Log exception from sync method.
                        continue;
                    }

                    if (task.IsCompleted)
                    {
                        if (task.Exception is not null)
                        {
                            exceptions.AddRange(task.Exception.InnerExceptions); // Log exception from async method.
                        }
                    }
                    else
                    {
                        tasks ??= new();
                        tasks.Add(Task.Run(() => task, token));
                    }
                }

                if (tasks is not null)
                {
                    Task groupedTasks = Task.WhenAll(tasks);

                    try
                    {
                        await groupedTasks.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        exceptions.AddRange(groupedTasks.Exception?.InnerExceptions ?? new[] { ex }.AsEnumerable());
                    }
                }
            }
            else
            {
                foreach (T service in services)
                {
                    try
                    {
                        await operation(service, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                        if (abortOnFirstException)
                        {
                            return;
                        }
                    }
                }
            }
        }

        private static List<IHostedLifecycleService>? GetHostLifecycles(IEnumerable<IHostedService> hostedServices)
        {
            List<IHostedLifecycleService>? _result = null;

            foreach (IHostedService hostedService in hostedServices)
            {
                if (hostedService is IHostedLifecycleService service)
                {
                    _result ??= new List<IHostedLifecycleService>();
                    _result.Add(service);
                }
            }

            return _result;
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            // The user didn't change the ContentRootFileProvider instance, we can dispose it
            if (ReferenceEquals(_hostEnvironment.ContentRootFileProvider, _defaultProvider))
            {
                // Dispose the content provider
                await DisposeAsync(_hostEnvironment.ContentRootFileProvider).ConfigureAwait(false);
            }
            else
            {
                // In the rare case that the user replaced the ContentRootFileProvider, dispose it and the one
                // we originally created
                await DisposeAsync(_hostEnvironment.ContentRootFileProvider).ConfigureAwait(false);
                await DisposeAsync(_defaultProvider).ConfigureAwait(false);
            }

            // Dispose the service provider
            await DisposeAsync(Services).ConfigureAwait(false);

            static async ValueTask DisposeAsync(object o)
            {
                switch (o)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }
    }
}
