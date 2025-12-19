using System;
using System.Collections.Generic;

namespace Revive.Core
{
    /// <summary>
    /// Provides a base implementation for managing services within an application. Implements the <see cref="IServiceRegistry"/> interface.
    /// </summary>
    /// <remarks>
    /// This class uses a dictionary to store services by their types. It is thread-safe.
    /// </remarks>
    public class ServiceRegistry : IServiceRegistry
    {
        private readonly Dictionary<Type, object> registeredService = new Dictionary<Type, object>();

        /// <inheritdoc />
        public event EventHandler<ServiceEventArgs> ServiceAdded;

        /// <inheritdoc />
        public event EventHandler<ServiceEventArgs> ServiceRemoved;

        /// <inheritdoc />
        public T GetService<T>()
            where T : class
        {
            var type = typeof(T);
            lock (registeredService)
            {
                if (registeredService.TryGetValue(type, out var service))
                    return (T)service;
            }

            return null;
        }

        /// <inheritdoc />
        /// <remarks>
        /// This implementation triggers the <see cref="ServiceAdded"/> event after a service is successfully added.
        /// </remarks>
        public void AddService<T>(T service)
            where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            var type = typeof(T);
            lock (registeredService)
            {
                if (registeredService.ContainsKey(type))
                    throw new ArgumentException("Service is already registered with this type", nameof(type));
                registeredService.Add(type, service);
            }
            OnServiceAdded(new ServiceEventArgs(type, service));
        }

        /// <inheritdoc />
        /// <remarks>
        /// This implementation triggers the <see cref="ServiceRemoved"/> event after a service is successfully removed.
        /// If the service type is not found, this method does nothing.
        /// </remarks>
        public void RemoveService<T>()
            where T : class
        {
            var type = typeof(T);
            object oldService;
            lock (registeredService)
            {
                if (registeredService.TryGetValue(type, out oldService))
                    registeredService.Remove(type);
            }
            if (oldService != null)
                OnServiceRemoved(new ServiceEventArgs(type, oldService));
        }

        private void OnServiceAdded(ServiceEventArgs e)
        {
            ServiceAdded?.Invoke(this, e);
        }

        private void OnServiceRemoved(ServiceEventArgs e)
        {
            ServiceRemoved?.Invoke(this, e);
        }
    }
}
