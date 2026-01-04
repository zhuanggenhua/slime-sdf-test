using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Revive.UI
{
    public class StartMenuController : MonoBehaviour
    {
        [SerializeField] private string startSceneName = "MainTest";

        [SerializeField] private Image startButtonImage;
        [SerializeField] private Image quitButtonImage;
        [Range(0f, 1f)]
        [SerializeField] private float buttonAlphaHitTestThreshold = 0.2f;

        private void Awake()
        {
            ApplyButtonAlphaHitTest();
        }

        private void OnEnable()
        {
            ApplyButtonAlphaHitTest();
        }

        private void ApplyButtonAlphaHitTest()
        {
            float t = Mathf.Clamp01(buttonAlphaHitTestThreshold);

            bool appliedAny = false;
            if (startButtonImage != null)
            {
                TryApplyAlphaHitTest(startButtonImage, t);
                appliedAny = true;
            }
            if (quitButtonImage != null)
            {
                TryApplyAlphaHitTest(quitButtonImage, t);
                appliedAny = true;
            }

            if (!appliedAny)
            {
                ApplyAlphaHitTestToBoundButtons(nameof(OnClickStart), t);
                ApplyAlphaHitTestToBoundButtons(nameof(OnClickQuit), t);
            }
        }

        private void ApplyAlphaHitTestToBoundButtons(string methodName, float threshold)
        {
            var buttons = FindObjectsOfType<Button>(true);
            if (buttons == null || buttons.Length == 0)
                return;

            for (int bi = 0; bi < buttons.Length; bi++)
            {
                var btn = buttons[bi];
                if (btn == null)
                    continue;

                var onClick = btn.onClick;
                int callCount = onClick.GetPersistentEventCount();
                for (int ci = 0; ci < callCount; ci++)
                {
                    if (onClick.GetPersistentTarget(ci) != this)
                        continue;
                    if (onClick.GetPersistentMethodName(ci) != methodName)
                        continue;

                    if (btn.targetGraphic is Image img)
                    {
                        TryApplyAlphaHitTest(img, threshold);
                    }

                    if (methodName == nameof(OnClickStart) && startButtonImage == null)
                        startButtonImage = btn.targetGraphic as Image;
                    if (methodName == nameof(OnClickQuit) && quitButtonImage == null)
                        quitButtonImage = btn.targetGraphic as Image;
                }
            }
        }

        private static void TryApplyAlphaHitTest(Image img, float threshold)
        {
            if (img == null)
                return;

            if (threshold <= 0f)
            {
                img.alphaHitTestMinimumThreshold = 0f;
                return;
            }

            if (!CanUseAlphaHitTest(img))
                return;

            try
            {
                img.alphaHitTestMinimumThreshold = threshold;
            }
            catch (InvalidOperationException)
            {
                // ignored
            }
        }

        private static bool CanUseAlphaHitTest(Image img)
        {
            if (img == null)
                return false;

            var sprite = img.sprite;
            if (sprite == null)
                return false;

            var tex = sprite.texture;
            if (tex == null)
                return false;
            if (!tex.isReadable)
                return false;

            if (tex is Texture2D t2d)
            {
                if (IsCrunchedFormat(t2d.format))
                    return false;
            }

            return true;
        }

        private static bool IsCrunchedFormat(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.ETC_RGB4Crunched:
                case TextureFormat.ETC2_RGBA8Crunched:
                    return true;
                default:
                    return false;
            }
        }

        public void OnClickStart()
        {
            Debug.Log($"[StartMenuController] OnClickStart (go='{gameObject.name}')");
            if (string.IsNullOrWhiteSpace(startSceneName))
            {
                Debug.LogError("StartMenuController: startSceneName is empty");
                return;
            }

            if (!Application.CanStreamedLevelBeLoaded(startSceneName))
            {
                Debug.LogError($"StartMenuController: Scene '{startSceneName}' cannot be loaded. Please add it to Build Settings (Scenes In Build) and ensure the name matches.");
                return;
            }

            SceneManager.LoadScene(startSceneName, LoadSceneMode.Single);
        }

        public void OnClickQuit()
        {
            Debug.Log($"[StartMenuController] OnClickQuit (go='{gameObject.name}')");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
