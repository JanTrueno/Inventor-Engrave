using System;
using System.Collections.Generic;
using Inventor;

namespace Engrave.Core
{
    /// <summary>
    /// Finds the "top" of a sheet metal part: the side with the inside of most bends ("bends up").
    /// Faces that join tangentially (a flat face and the bend cylinder it runs into) belong to the same side, so the two
    /// largest groups of joined faces are the two sides of the sheet. At every bend (two concentric cylinders whose radii
    /// differ by the thickness) the smaller cylinder is the inside. The side with the most insides has the most bends up.
    /// </summary>
    internal sealed class TopSide
    {
        /// <summary>Transient keys of the flat faces that may be engraved.</summary>
        public readonly HashSet<int> Faces = new HashSet<int>();
        public string Info = "";

        public bool Allows(Face f)
        {
            try { return Faces.Count == 0 || Faces.Contains(f.TransientKey); } catch { return true; }
        }

        public static TopSide Find(PartDocument part, bool topIsInside)
        {
            try { return Compute(part, topIsInside); }
            catch (Exception ex)
            {
                Log.Write("Finding the top side of " + part.FullDocumentName + " failed: " + ex);
                TopSide all = AllFlat(part);
                all.Info = "Top side not found (" + ex.Message + "): every flat face can be picked.";
                return all;
            }
        }

        private static TopSide AllFlat(PartDocument part)
        {
            var t = new TopSide();
            try
            {
                foreach (Face f in part.ComponentDefinition.SurfaceBodies[1].Faces)
                    if (f.SurfaceType == SurfaceTypeEnum.kPlaneSurface) t.Faces.Add(f.TransientKey);
            }
            catch { /* an empty set allows every face */ }
            return t;
        }

        private static TopSide Compute(PartDocument part, bool topIsInside)
        {
            var def = (SheetMetalComponentDefinition)part.ComponentDefinition;
            SurfaceBody body = def.SurfaceBodies[1];
            double thickness = Convert.ToDouble(def.Thickness.Value);   // cm

            var parent = new Dictionary<int, int>();
            var faces = new Dictionary<int, Face>();
            var flat = new List<int>();
            foreach (Face f in body.Faces)
            {
                int k = f.TransientKey;
                faces[k] = f;
                parent[k] = k;
                if (f.SurfaceType == SurfaceTypeEnum.kPlaneSurface) flat.Add(k);
            }

            // join each flat face with the bend cylinders it runs into tangentially
            foreach (Face f in body.Faces)
            {
                foreach (Edge e in f.Edges)
                {
                    try
                    {
                        if (e.Faces.Count != 2) continue;
                        Face fa = e.Faces[1], fb = e.Faces[2];
                        Plane pl = fa.Geometry as Plane;
                        Cylinder cy = fb.Geometry as Cylinder;
                        if (pl == null || cy == null)
                        {
                            pl = fb.Geometry as Plane;
                            cy = fa.Geometry as Cylinder;
                        }
                        if (pl != null && cy != null && Touches(pl, cy)) Union(parent, fa.TransientKey, fb.TransientKey);
                    }
                    catch { /* edge without usable geometry */ }
                }
            }

            var area = new Dictionary<int, double>();
            foreach (KeyValuePair<int, Face> kv in faces)
            {
                int root = Root(parent, kv.Key);
                double a = 0;
                try { a = kv.Value.Evaluator.Area; } catch { }
                area[root] = (area.TryGetValue(root, out double sum) ? sum : 0) + a;
            }

            var roots = new List<int>(area.Keys);
            roots.Sort((x, y) => area[y].CompareTo(area[x]));
            TopSide result;
            if (roots.Count < 2)
            {
                result = AllFlat(part);
                result.Info = "Sides not found: every flat face can be picked.";
                return result;
            }
            int r1 = roots[0], r2 = roots[1];
            if (area[r2] < 0.65 * area[r1])
            {
                result = AllFlat(part);
                result.Info = "The two sides cannot be told apart: every flat face can be picked.";
                return result;
            }
            double total = 0;
            foreach (double a in area.Values) total += a;
            if (area[r1] + area[r2] < 0.5 * total)
            {
                result = AllFlat(part);
                result.Info = "The sides cannot be found reliably: every flat face can be picked.";
                return result;
            }

            var cyl1 = new List<Cylinder>();
            var cyl2 = new List<Cylinder>();
            foreach (KeyValuePair<int, Face> kv in faces)
            {
                if (kv.Value.SurfaceType != SurfaceTypeEnum.kCylinderSurface) continue;
                if (!(kv.Value.Geometry is Cylinder c)) continue;
                int root = Root(parent, kv.Key);
                if (root == r1) cyl1.Add(c);
                else if (root == r2) cyl2.Add(c);
            }

            int b1 = 0, b2 = 0;   // bends with their inside on side 1 / side 2
            foreach (Cylinder g1 in cyl1)
            {
                foreach (Cylinder g2 in cyl2)
                {
                    if (!SameBend(g1, g2, thickness)) continue;
                    if (g1.Radius < g2.Radius) b1++; else b2++;
                    break;
                }
            }

            result = new TopSide();
            if (b1 == b2)
            {
                foreach (int k in flat)
                {
                    int root = Root(parent, k);
                    if (root == r1 || root == r2) result.Faces.Add(k);
                }
                result.Info = b1 == 0 ? "No bends: both sides can be picked."
                                      : "As many bends up as down (" + b1 + " / " + b2 + "): both sides can be picked.";
                return result;
            }

            bool side1 = b1 > b2;
            if (!topIsInside) side1 = !side1;
            int top = side1 ? r1 : r2;
            foreach (int k in flat) if (Root(parent, k) == top) result.Faces.Add(k);
            result.Info = "Top side: the side with most bends up (" + Math.Max(b1, b2) + " of " + (b1 + b2) + "). Only that side can be picked.";
            return result;
        }

        /// <summary>A flat face touches a cylinder when the axis is parallel to the plane at a distance of one radius.</summary>
        private static bool Touches(Plane p, Cylinder c)
        {
            Vector n = p.Normal.AsVector();
            Vector a = c.AxisVector.AsVector();
            if (Math.Abs(n.DotProduct(a)) > 0.001) return false;
            Vector v = p.RootPoint.VectorTo(c.BasePoint);
            return Math.Abs(Math.Abs(v.DotProduct(n)) - c.Radius) < 0.005;
        }

        /// <summary>The inside and outside of one bend: same axis, radii that differ by the sheet thickness.</summary>
        private static bool SameBend(Cylinder g1, Cylinder g2, double thickness)
        {
            Vector a1 = g1.AxisVector.AsVector();
            Vector a2 = g2.AxisVector.AsVector();
            if (a1.CrossProduct(a2).Length > 0.001) return false;
            Vector v = g1.BasePoint.VectorTo(g2.BasePoint);
            if (v.CrossProduct(a1).Length > 0.005) return false;
            return Math.Abs(Math.Abs(g1.Radius - g2.Radius) - thickness) < 0.01;
        }

        private static int Root(Dictionary<int, int> parent, int k)
        {
            while (parent[k] != k)
            {
                parent[k] = parent[parent[k]];
                k = parent[k];
            }
            return k;
        }

        private static void Union(Dictionary<int, int> parent, int a, int b)
        {
            int ra = Root(parent, a), rb = Root(parent, b);
            if (ra != rb) parent[ra] = rb;
        }
    }
}
