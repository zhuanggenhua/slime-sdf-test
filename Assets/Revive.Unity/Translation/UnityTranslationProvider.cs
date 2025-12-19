using System;
using System.Collections.Generic;
using System.Reflection;
using Revive.Core;
using Revive.Core.Annotations;
using Revive.Core.Translation;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

namespace Revive.Unity
{
    public class UnityTranslationProvider : ITranslationProvider
    {
        private readonly Dictionary<(string table, string key), LocalizedString> _texts = new(64);
        
        public EventChannel LocaleChangedEvent { get; } = new EventChannel();
        
        public const string ZhStr = "Chinese (Simplified) (zh)";
        public const string EnStr = "English (en)";
        
        public const string DefaultContext = "Common";
        
        public UnityTranslationProvider()
            : this(Assembly.GetCallingAssembly())
        {
        }

        public UnityTranslationProvider([NotNull] Assembly assembly)
            : this(assembly.GetName().Name, assembly)
        {
            
        }

        private UnityTranslationProvider([NotNull] string baseName, [NotNull] Assembly assembly)
        {
            if (baseName == null) throw new ArgumentNullException(nameof(baseName));
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            
            BaseName = baseName;
            
            // when unity locale changed, trigger the event
            LocalizationSettings.SelectedLocaleChanged += locale =>
            {
                LocaleChangedEvent.RaiseEvent();
            };
            
            LocalizationSettings.StringDatabase.MissingTranslationState =
                MissingTranslationBehavior.ShowMissingTranslationMessage;
            LocalizationSettings.StringDatabase.NoTranslationFoundMessage = "{key}";
        }
        
        private LocalizedString Text(string table, string key, Action<string> onStringChanged = null)
        {
            if (_texts.TryGetValue((table, key), out var text))
            {
                if (onStringChanged != null)
                {
                    text.StringChanged += str => onStringChanged(str);
                }
            }
            else
            {
                text = new LocalizedString(table, key);
                if (onStringChanged != null)
                {
                    text.StringChanged += str => onStringChanged(str);
                }
                _texts.Add((table, key), text);
            }
            return text;
        }
        
        /// <inheritdoc />
        public string BaseName { get; }
        
        /// <inheritdoc />
        public string GetString(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return Text(DefaultContext, text).GetLocalizedStringAsync().WaitForCompletion();
        }

        /// <inheritdoc />
        public string GetPluralString(string text, string textPlural, long count)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            // Note: plurals not supported by ResourceManager, fallback to GetString
            return GetString(text);
        }

        /// <inheritdoc />
        public string GetParticularString(string context, string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return Text(context, text).GetLocalizedStringAsync().WaitForCompletion();
        }

        /// <inheritdoc />
        public string GetParticularPluralString(string context, string text, string textPlural, long count)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            // Note: plurals not supported by ResourceManager, fallback to GetParticularPluralString
            return GetParticularString(context, text);
        }
    }
}