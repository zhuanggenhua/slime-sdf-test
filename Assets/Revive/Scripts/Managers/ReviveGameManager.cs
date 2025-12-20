using System;
using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using Revive.Core.Pool;
using Revive.Core.Translation;
using Revive.Unity;
using UnityEngine;

namespace Revive
{
    public class ReviveGameManager : GameManager
    {
        private Framework _framework;
        
        protected override void Awake()
        {
            base.Awake();
            
            _framework = new Framework();
            
            TranslationManager.Instance.RegisterProvider(new UnityTranslationProvider());
        }

        private void Update()
        {
            Debug.Log(Tr._p("Common", "Continue"));
        }
    }
}