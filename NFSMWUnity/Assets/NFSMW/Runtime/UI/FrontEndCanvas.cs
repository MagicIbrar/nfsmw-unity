using UnityEngine;
using UnityEngine.UI;

namespace NfsMw.UI
{
    /// <summary>Canvas scaled like the game's front end: 640x480 design space fitted to the screen height.</summary>
    public static class FrontEndCanvas
    {
        public static Canvas Create(string name = "Front End", Camera renderCamera = null, int sortOrder = 10)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var canvas = go.AddComponent<Canvas>();
            if (renderCamera != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = renderCamera;
                canvas.planeDistance = 1f;
            }
            else canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(640, 480);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            return canvas;
        }
    }
}
