using System;
using Revive.Core.Annotations;

namespace Revive.Core
{
    /// <summary>
    /// A service registry is a <see cref="IServiceProvider"/> that provides methods to register and unregister services.
    /// </summary>
    public interface IServiceRegistry
    {
        /// <summary>
        /// Occurs when a new service is added to the registry.
        /// </summary>
        event EventHandler<ServiceEventArgs> ServiceAdded;

        /// <summary>
        /// Occurs when a service is removed from the registry.
        /// </summary>
        event EventHandler<ServiceEventArgs> ServiceRemoved;

        /// <summary>
        /// Adds a service to this <see cref="ServiceRegistry"/>.
        /// </summary>
        /// <typeparam name="T">The type of service to add.</typeparam>
        /// <param name="service">The service to add.</param>
        /// <exception cref="ArgumentNullException">Thrown when the provided service is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a service of the same type is already registered.</exception>
        void AddService<T>([NotNull] T service) where T : class;

        /// <summary>
        /// Gets the service object of the specified type.
        /// </summary>
        /// <typeparam name="T">The type of the service to retrieve.</typeparam>
        /// <returns>A service of the requested type, or [null] if not found.</returns>
        [CanBeNull]
        T GetService<T>() where T : class;

        /// <summary>
        /// Removes the object providing a specified service.
        /// </summary>
        /// <typeparam name="T">The type of the service to remove.</typeparam>
        void RemoveService<T>() where T : class;
    }
}
