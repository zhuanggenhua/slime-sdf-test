using Revive.Core;

namespace Revive.Unity
{
    public class Framework
    {
        private readonly IServiceRegistry _services;
        
        public IServiceRegistry Services => _services;
        
        public Framework()
        {
            _services = new ServiceRegistry();
        }
    }
}