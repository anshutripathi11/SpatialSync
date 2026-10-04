using System.Collections.Generic;
using UnityEngine;

namespace FloorTrack.Twin
{
    /// <summary>
    /// Pure mesh generation for one wall:
    ///   • BuildSolid: the wall as boxes, with doors/windows/openings cut out.
    ///   • BuildPhotoPatch: a grid laid on the wall face that faces the photo's camera, with UVs computed by
    ///     projecting each vertex back into the photo (projective texturing), clipped to what the photo
    ///     actually covers and skipping holes. Only the stretch [t0, t1] that photo saw is covered.
    /// </summary>
    public static class WallMeshBuilder
    {
        public struct Opening { public float s0, s1, bottom, top; }   // metres along the wall / height

        public class Frame
        {
            public Vector3 A, U, N;        // start (floor level), unit direction, unit normal (right side)
            public float Length, Thickness, Height;

            public Frame(Vector3 a, Vector3 b, float thickness, float height)
            {
                A = new Vector3(a.x, 0, a.z);
                Vector3 d = new Vector3(b.x - a.x, 0, b.z - a.z);
                Length = d.magnitude;
                U = Length > 1e-5f ? d / Length : Vector3.right;
                N = new Vector3(U.z, 0, -U.x);
                Thickness = Mathf.Max(0.02f, thickness);
                Height = height;
            }

            public Vector3 Point(float s, float y, float side) => A + U * s + N * side + Vector3.up * y;
        }

        // ------------------------------------------------------------------ solid wall
        public static Mesh BuildSolid(Frame f, IList<Opening> openings)
        {
            var cuts = new List<float> { 0f, f.Length };
            foreach (var o in openings)
            {
                cuts.Add(Mathf.Clamp(o.s0, 0, f.Length));
                cuts.Add(Mathf.Clamp(o.s1, 0, f.Length));
            }
            cuts.Sort();

            var mb = new MeshData();
            float ht = f.Thickness * 0.5f;
            for (int i = 0; i < cuts.Count - 1; i++)
            {
                float x0 = cuts[i], x1 = cuts[i + 1];
                if (x1 - x0 < 1e-3f) continue;
                float xc = (x0 + x1) * 0.5f;

                // Solid vertical ranges = [0, H] minus every opening that covers this slice.
                var holes = new List<Vector2>();
                foreach (var o in openings)
                    if (o.s0 <= xc && xc <= o.s1) holes.Add(new Vector2(Mathf.Max(0, o.bottom), Mathf.Min(f.Height, o.top)));
                holes.Sort((p, q) => p.x.CompareTo(q.x));

                float y = 0f;
                foreach (var h in holes)
                {
                    if (h.x > y + 1e-3f) mb.AddBox(f, x0, x1, y, h.x, ht);
                    y = Mathf.Max(y, h.y);
                }
                if (f.Height > y + 1e-3f) mb.AddBox(f, x0, x1, y, f.Height, ht);
            }
            return mb.ToMesh("Wall");
        }

        // ------------------------------------------------------------------ projected photo patch
        public static Mesh BuildPhotoPatch(Frame f, PhotoCamera cam, float s0, float s1, IList<Opening> openings,
                                           float metersPerColumn = 0.2f, int rows = 16)
        {
            // Which face does the camera see?
            float side = Mathf.Sign(Vector3.Dot(cam.Position - f.A, f.N));
            if (side == 0) side = 1;
            float offset = side * (f.Thickness * 0.5f + 0.006f);

            int cols = Mathf.Clamp(Mathf.CeilToInt((s1 - s0) / metersPerColumn), 2, 96);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var colOk = new bool[cols + 1];
            var colY = new Vector2[cols + 1];

            for (int i = 0; i <= cols; i++)
            {
                float s = Mathf.Lerp(s0, s1, (float)i / cols);
                Vector3 floorPt = f.Point(s, 0, offset);
                colOk[i] = cam.VerticalRange(floorPt, f.Height, out float yMin, out float yMax);
                colY[i] = new Vector2(yMin, yMax);
                for (int j = 0; j <= rows; j++)
                {
                    float y = Mathf.Lerp(yMin, yMax, (float)j / rows);
                    Vector3 p = f.Point(s, y, offset);
                    bool inFront = cam.Project(p, out Vector2 uv);
                    colOk[i] &= inFront;
                    verts.Add(p);
                    uvs.Add(uv);
                }
            }

            int stride = rows + 1;
            for (int i = 0; i < cols; i++)
            {
                if (!colOk[i] || !colOk[i + 1]) continue;
                float sc = Mathf.Lerp(s0, s1, (i + 0.5f) / cols);
                for (int j = 0; j < rows; j++)
                {
                    float yc = Mathf.Lerp((colY[i].x + colY[i + 1].x) * 0.5f, (colY[i].y + colY[i + 1].y) * 0.5f, (j + 0.5f) / rows);
                    if (InsideOpening(openings, sc, yc)) continue;
                    int v00 = i * stride + j, v01 = v00 + 1, v10 = v00 + stride, v11 = v10 + 1;
                    AddQuadFacing(tris, verts, v00, v10, v11, v01, f.N * side);
                }
            }

            var m = new Mesh { name = "PhotoPatch" };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static bool InsideOpening(IList<Opening> openings, float s, float y)
        {
            foreach (var o in openings)
                if (o.s0 <= s && s <= o.s1 && o.bottom <= y && y <= o.top) return true;
            return false;
        }

        /// <summary>Adds quad (a,b,c,d) with the winding that makes it visible from the 'facing' side.</summary>
        private static void AddQuadFacing(List<int> tris, List<Vector3> v, int a, int b, int c, int d, Vector3 facing)
        {
            Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (Vector3.Dot(n, facing) >= 0) { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(d); }
            else { tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(a); tris.Add(d); tris.Add(c); }
        }

        // ------------------------------------------------------------------ small mesh helper
        public class MeshData
        {
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<Vector3> n = new List<Vector3>();
            readonly List<int> t = new List<int>();

            public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
            {
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                for (int k = 0; k < 4; k++) n.Add(outward);
                Vector3 cr = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(cr, outward) >= 0) { t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3); }
                else { t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i); t.Add(i + 3); t.Add(i + 2); }
            }

            public void AddBox(Frame f, float x0, float x1, float y0, float y1, float halfT)
            {
                Vector3 P(float s, float y, float side) => f.Point(s, y, side);
                // two big faces
                AddQuad(P(x0, y0, halfT), P(x1, y0, halfT), P(x1, y1, halfT), P(x0, y1, halfT), f.N);
                AddQuad(P(x0, y0, -halfT), P(x0, y1, -halfT), P(x1, y1, -halfT), P(x1, y0, -halfT), -f.N);
                // ends
                AddQuad(P(x0, y0, -halfT), P(x0, y0, halfT), P(x0, y1, halfT), P(x0, y1, -halfT), -f.U);
                AddQuad(P(x1, y0, halfT), P(x1, y0, -halfT), P(x1, y1, -halfT), P(x1, y1, halfT), f.U);
                // top / bottom
                AddQuad(P(x0, y1, halfT), P(x1, y1, halfT), P(x1, y1, -halfT), P(x0, y1, -halfT), Vector3.up);
                AddQuad(P(x0, y0, -halfT), P(x1, y0, -halfT), P(x1, y0, halfT), P(x0, y0, halfT), Vector3.down);
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v);
                m.SetNormals(n);
                m.SetTriangles(t, 0);
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
