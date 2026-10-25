using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// The player profile's camera options (GOJumpCams 0x51a9d0 / GORearview 0x51a9f0 in the FE options): jump cams gate
    /// every scripted camera shot (0x469e30 reads profile+0x47), the rear-view mirror is drawn in the bumper and hood
    /// views (RearViewMirrorCameraMover::Update 0x476690 reads profile+0x39). Both live in the FE database's gameplay
    /// settings (FEDb, Options → Gameplay), defaults on as in cFrontendDatabase.
    /// </summary>
    public static class CameraOptions
    {
        public static bool JumpCams
        {
            get => UI.FEDb.Data.Gameplay.JumpCam;
            set => UI.FEDb.Data.Gameplay.JumpCam = value;
        }

        public static bool RearViewMirror
        {
            get => UI.FEDb.Data.Gameplay.RearviewOn;
            set => UI.FEDb.Data.Gameplay.RearviewOn = value;
        }
    }

    /// <summary>
    /// Feeds the player car's collisions to its camera (CDActionDrive::OnCollision 0x472460): a type-2 hit over 5 m/s
    /// sets the 2 s CollisionDamping timer to clamp((v − 5)·0.025)·2, which lengthens the heading lag and arms the
    /// camera's impact lurch.
    /// </summary>
    public sealed class CameraCollisionRelay : MonoBehaviour
    {
        /// <summary>Seconds left on the collision timer (decays 1/s; the anchor sees it / 2).</summary>
        public float Timer;

        void Update() { if (Timer > 0) Timer = Mathf.Max(0, Timer - Time.unscaledDeltaTime); }

        void OnCollisionEnter(Collision c)
        {
            float v = c.relativeVelocity.magnitude;
            if (v <= 5f) return;
            Timer = Mathf.Max(Timer, Mathf.Clamp01((v - 5f) * 0.025f) * 2f);
        }
    }

    /// <summary>
    /// RearViewMirrorCameraMover (speed.exe 0x476690; view 2): the car's frame turned to face backwards, the eye 0.5 m
    /// above the car origin [?], FOV bAngle 20000 (109.9°), near 0.75, far 192 (0x8EDED8). Drawn as a strip at the top of
    /// the screen while the bumper or hood camera is on and the option is set.
    /// </summary>
    public sealed class RearViewMirror : MonoBehaviour
    {
        public Transform Target;
        Camera _cam;
        RenderTexture _rt;
        Canvas _canvas;

        public static RearViewMirror Create(Transform parent)
        {
            var go = new GameObject("Rear View Mirror");
            go.transform.SetParent(parent, false);
            var m = go.AddComponent<RearViewMirror>();
            m._cam = go.AddComponent<Camera>();
            m._cam.nearClipPlane = 0.75f;
            m._cam.farClipPlane = 192f;
            m._cam.depth = 5;
            m._rt = new RenderTexture(512, 128, 16) { name = "RearView" };
            m._cam.targetTexture = m._rt;
            var cgo = new GameObject("Mirror Canvas", typeof(Canvas));
            cgo.transform.SetParent(go.transform, false);
            m._canvas = cgo.GetComponent<Canvas>();
            m._canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m._canvas.sortingOrder = 2;
            var img = new GameObject("Mirror", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.RawImage));
            var rt = (RectTransform)img.transform;
            rt.SetParent(cgo.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -12f);
            rt.sizeDelta = new Vector2(384f, 96f);
            var raw = img.GetComponent<UnityEngine.UI.RawImage>();
            raw.texture = m._rt;
            raw.uvRect = new Rect(1, 0, -1, 1);   // a mirror shows the world flipped left-right
            raw.raycastTarget = false;
            return m;
        }

        public void Show(bool on)
        {
            if (_cam) _cam.enabled = on;
            if (_canvas) _canvas.enabled = on;
        }

        void LateUpdate()
        {
            if (!Target || !_cam || !_cam.enabled) return;
            transform.position = Target.position + Target.up * 0.5f;
            transform.rotation = Quaternion.LookRotation(-Target.forward, Target.up);
            ChaseCamera.ApplyProjection(_cam, 20000f * 360f / 65536f, 1f);
        }

        void OnDestroy() { if (_rt) _rt.Release(); }
    }
}
