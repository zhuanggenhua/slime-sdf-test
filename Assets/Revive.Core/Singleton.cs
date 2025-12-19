namespace Revive.Core
{
    /// <summary>
    /// Base class for singletons.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class Singleton<T> where T : new()
    {
        private static T _instance = new T();

        protected Singleton()
        {
            
        }

        public static T Instance => _instance;
    }
}