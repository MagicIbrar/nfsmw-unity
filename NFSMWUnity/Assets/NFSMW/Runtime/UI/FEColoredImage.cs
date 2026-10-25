using UnityEngine;
using UnityEngine.UI;

namespace NfsMw.UI
{
    /// <summary>
    /// FEColoredImage (object type 9, FEng/FEColoredImage.h): an image whose four corners carry their own colour.
    /// cFEngRender::RenderCBVImage (PC 0x59A790) packs only the four vertex colours (FEColoredImageData +0x54, ints B, G, R, A)
    /// into the poly — the object's own colour and its parents' are not used — in AddPoly's vertex order TL, TR, BR, BL
    /// (0x582660). FEView sets <see cref="Corners"/> every layout.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FEColoredImage : RawImage
    {
        /// <summary>Top-left, top-right, bottom-right, bottom-left (FE screen orientation, +Y down).</summary>
        public readonly Color[] Corners = { Color.white, Color.white, Color.white, Color.white };

        public void SetCorners(Color tl, Color tr, Color br, Color bl)
        {
            if (Corners[0] == tl && Corners[1] == tr && Corners[2] == br && Corners[3] == bl) return;
            Corners[0] = tl; Corners[1] = tr; Corners[2] = br; Corners[3] = bl;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = GetPixelAdjustedRect();
            var uv = uvRect;
            // Unity's +Y is up: the FE top edge is r.yMax
            vh.AddVert(new Vector3(r.xMin, r.yMax), Corners[0], new Vector2(uv.xMin, uv.yMax));
            vh.AddVert(new Vector3(r.xMax, r.yMax), Corners[1], new Vector2(uv.xMax, uv.yMax));
            vh.AddVert(new Vector3(r.xMax, r.yMin), Corners[2], new Vector2(uv.xMax, uv.yMin));
            vh.AddVert(new Vector3(r.xMin, r.yMin), Corners[3], new Vector2(uv.xMin, uv.yMin));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(0, 2, 3);
        }
    }
}
