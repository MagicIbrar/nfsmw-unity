using UnityEngine;

namespace NfsMw.CarRender
{
    /// <summary>
    /// The drawn wheel position of CarRenderInfo::Render (0x74F048, CarRender.cpp:3343-3385; Core WheelRenderGeometry):
    /// the exe draws each wheel at the ecar TireOffsets x and the trans_axle y — car-space translations applied after the
    /// tyre matrix's steer / camber / roll, which therefore turn the wheel about its own drawn centre — while the sim's
    /// wheel (the pivot PlayerCar / TrafficVehicle / NisCarControl move) stands on the chassis TRACK_WIDTH / FRONT_AXLE.
    /// The pivot keeps the sim position (its height = the sim's compression, as the exe's tyre matrix z); this component
    /// moves the Spin and Brake children by the car-space offset ecar − sim, undoing the pivot's rotation so the offset
    /// stays a car-space translation, and keeps the extra rear wheel of ExtraRearTireOffset (Render, CarRender.cpp:3558-3612: tire2/3.v3.x
    /// += offset, drawn with the rear model; semi 1.5, trafcemtr / cemtr 0.93, trafdmptr −1) spinning with the wheel.
    /// Runs after CarRenderConn (order 50), which sets the pivot rotation in its LateUpdate.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class WheelRenderOffset : MonoBehaviour
    {
        /// <summary>ecar drawn centre − sim pivot, in car (root) space.</summary>
        public Vector3 Offset;
        public Transform Spin, Brake, ExtraSpin;
        /// <summary>The brake's offset from the drawn centre in pivot space (brake_trans_pivot, turned with the wheel).</summary>
        public Vector3 BrakeBase;
        /// <summary>ExtraRearTireOffset along the car's forward axis (game x).</summary>
        public float ExtraForward;

        void LateUpdate() => Apply();

        public void Apply()
        {
            var inv = Quaternion.Inverse(transform.localRotation);
            var off = inv * Offset;
            if (Spin) Spin.localPosition = off;
            if (Brake) Brake.localPosition = off + BrakeBase;
            if (ExtraSpin)
            {
                ExtraSpin.localPosition = inv * (Offset + new Vector3(0f, 0f, ExtraForward));
                if (Spin) ExtraSpin.localRotation = Spin.localRotation;
            }
        }
    }
}
