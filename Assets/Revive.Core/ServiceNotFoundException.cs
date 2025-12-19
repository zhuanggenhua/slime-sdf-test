using System;
using Revive.Core.Annotations;

namespace Revive.Core
{
    public class ServiceNotFoundException : Exception
    {
        public ServiceNotFoundException()
        {
        }

        public ServiceNotFoundException([NotNull] Type serviceType)
            : base(FormatServiceNotFoundMessage(serviceType))
        {
            ServiceType = serviceType;
        }

        public ServiceNotFoundException([NotNull] Type serviceType, Exception innerException)
            : base(FormatServiceNotFoundMessage(serviceType), innerException)
        {
            ServiceType = serviceType;
        }

        public Type ServiceType { get; private set; }

        [NotNull]
        private static string FormatServiceNotFoundMessage([NotNull] Type serviceType)
        {
            return $"Service [{serviceType.Name}] not found";
        }
    }
}
