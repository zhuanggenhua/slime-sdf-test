using System;
using System.Collections.Generic;

namespace Revive.Core
{
    public interface IEvent
    {
        IUnregister Register(Action listener);
        void Unregister(Action listener);
    }
    
    public interface IEvent<T>
    {
        IUnregister Register(Action<T> listener);
        void Unregister(Action<T> listener);
    }
    
    public interface IEventChannel : IEvent
    {
        void RaiseEvent();
    }
    
    public interface IEventChannel<T> : IEvent<T>
    {
        void RaiseEvent(T value);
    }
    
    public interface IUnregister
    {
        void Unregister();
    }

    public interface IUnregisterList
    {
        List<IUnregister> UnregisterList { get; }
    }

    public static class UnregisterListExtension
    {
        public static void AddToUnregisterList(this IUnregister self, IUnregisterList unregisterList) =>
            unregisterList.UnregisterList.Add(self);

        public static void UnregisterAll(this IUnregisterList self)
        {
            foreach (var unRegister in self.UnregisterList)
            {
                unRegister.Unregister();
            }

            self.UnregisterList.Clear();
        }

        public static void Register(this IEvent e, Action listener, IUnregisterList unregisterList) => 
            e.Register(listener).AddToUnregisterList(unregisterList);
        
        public static void Register<T>(this IEvent<T> e, Action<T> listener, IUnregisterList unregisterList) =>
            e.Register(listener).AddToUnregisterList(unregisterList);
    }

    public struct CustomUnregister : IUnregister
    {
        private Action _onUnregister;
        public CustomUnregister(Action onUnregister) => _onUnregister = onUnregister;

        public void Unregister()
        {
            _onUnregister.Invoke();
            _onUnregister = null;
        }
    }

    public readonly ref struct InBlockUnregister
    {
        private readonly IUnregister _onUnregister;
        public InBlockUnregister(IUnregister onUnregister) => _onUnregister = onUnregister;

        public void Dispose()
        {
            _onUnregister.Unregister();
        }
    }
    
    public class CustomUnregisterList : IUnregisterList
    {
        public List<IUnregister> UnregisterList { get; } = new List<IUnregister>();
    }

    public interface IBindableProperty
    {
        object GetObject();
        void SetObject(object value);
        void SetObjectWithoutNotify(object value);
        void UnregisterAll();
    }
    
    public interface IBindableProperty<T> : IEventChannel<T>
    {
        new T GetValue();
        void SetValue(T value);
        void SetValueWithoutNotify(T value);
    }
    
    public class BindableProperty<T> : BindablePropertyBase<T>
    {
        public BindableProperty(T defaultValue = default) => _value = defaultValue;

        protected T _value;

        public override T GetValue() => _value;

        public override void SetValueWithoutNotify(T newValue) => _value = newValue;
    }
    
    public abstract class BindablePropertyBase<T> : IBindableProperty, IBindableProperty<T>
    {
        public Func<T, T, bool> Comparer { get; set; } = (a, b) => a.Equals(b);
        
        public object GetObject() => GetValue();
        public void SetObject(object value) => SetValue((T)value);

        public void SetObjectWithoutNotify(object value) => SetValueWithoutNotify((T)value);

        private static readonly Action<T> Dummy = _ => { };
        
        public void UnregisterAll()
        {
            _onValueChanged = Dummy;
        }

        public abstract T GetValue();

        public virtual void SetValueWithoutNotify(T newValue){}

        public void SetValue(T value)
        {
            if (value == null && GetValue() == null) return;
            if (value != null && Comparer(value, GetValue())) return;

            SetValueWithoutNotify(value);
            _onValueChanged.Invoke(value);
        }

        private Action<T> _onValueChanged = _ => { };

        public IUnregister Register(Action<T> onValueChanged)
        {
            _onValueChanged += onValueChanged;
            return new CustomUnregister(() => Unregister(onValueChanged));
        }

        public IUnregister RegisterWithInitValue(Action<T> onValueChanged)
        {
            onValueChanged(GetValue());
            return Register(onValueChanged);
        }

        public void Unregister(Action<T> onValueChanged) => _onValueChanged -= onValueChanged;

        public void RaiseEvent(T value)
        {
            _onValueChanged.Invoke(value);
        }

        public override string ToString() => GetValue().ToString();
    }
    
    public class EventChannel<T> : IEventChannel<T>
    {
        public Action<T> OnEventRaised;
        public bool Mute = false;

        public IUnregister Register(Action<T> listener)
        {
            OnEventRaised += listener;
            return new CustomUnregister(() => Unregister(listener));
        }

        public void Unregister(Action<T> listener)
        {
            OnEventRaised -= listener;
        }

        public void RaiseEvent(T value)
        {
            if (Mute) return;
            if (OnEventRaised != null)
                OnEventRaised.Invoke(value);
        }
    }
    
    public class EventChannel : IEventChannel
    {
        public Action OnEventRaised;

        public IUnregister Register(Action listener)
        {
            OnEventRaised += listener;
            return new CustomUnregister(() => Unregister(listener));
        }

        public void Unregister(Action listener)
        {
            OnEventRaised -= listener;
        }

        public void RaiseEvent()
        {
            if (OnEventRaised != null)
                OnEventRaised.Invoke();
        }
    }
}