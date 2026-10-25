using UnityEngine;

namespace NfsMw.CarRender
{
    /// <summary>
    /// One live vehicle fragment (DrawVehicle::Part + VehicleFragmentConn, car_render.md §11): counts against
    /// Vehicle_Part_Count while it exists and releases itself after KILL_OFF_SCREEN seconds unseen
    /// (DrawVehicle::Part::OnProcessFrame → ReleaseModel), also once its car is gone.
    /// </summary>
    public sealed class CarFragment : MonoBehaviour
    {
        /// <summary>Vehicle_Part_Count: live parts of all vehicles (SpawnModel refuses at 61, DrawVehicle.cpp:207).</summary>
        public static int Count { get; private set; }

        public uint PartName, System;
        public float KillOffScreen = 8f;
        public Joint Joint;
        Renderer[] _renderers;
        float _offScreen;

        void Awake() => Count++;

        void OnDestroy() => Count--;

        void Update()
        {
            if (KillOffScreen <= 0f) return;
            if (_renderers == null) _renderers = GetComponentsInChildren<Renderer>();
            bool inView = false;
            foreach (var r in _renderers) if (r && r.isVisible) { inView = true; break; }
            _offScreen = inView ? 0f : _offScreen + Time.deltaTime;
            if (_offScreen > KillOffScreen) Destroy(gameObject);
        }
    }
}
