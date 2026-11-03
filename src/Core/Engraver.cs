using System;
using System.Collections.Generic;
using System.Globalization;
using Inventor;

namespace Engrave.Core
{
    /// <summary>
    /// Puts the text as a sketch on a face, engraves it with Inventor's Emboss (Engrave from Face), and finds, edits or
    /// deletes engravings made earlier. On a round face the sketch goes on a work plane tangent to the face at the picked
    /// point and the engraving is wrapped onto the face. The names are those of the iLogic rule this add-in replaces, so
    /// engravings made by the rule (extrude cuts) are found too.
    /// </summary>
    internal static class Engraver
    {
        public const string FeatureName = "Graveer";
        public const string SketchName = "GraveerSketch";
        private const string PlaneName = "GraveerVlak";
        private const string NewSuffix = " (new)";
        private const string EngravedProperty = "Graveren";

        // ----- finding ------------------------------------------------------------------------------------------

        /// <summary>The engraving feature of the part (an emboss, or an extrude cut made by the old rule), or null.</summary>
        public static PartFeature Find(PartDocument part)
        {
            try
            {
                foreach (PartFeature f in part.ComponentDefinition.Features)
                    if (f.Name == FeatureName && (f is EmbossFeature || f is ExtrudeFeature)) return f;
            }
            catch { /* part not readable */ }
            return null;
        }

        public static PlanarSketch SketchOf(PartFeature f)
        {
            try
            {
                if (f is EmbossFeature emboss) return emboss.Profile.Parent as PlanarSketch;
                if (f is ExtrudeFeature extrude) return extrude.Profile.Parent as PlanarSketch;
            }
            catch { }
            return null;
        }

        /// <summary>The engraved text as one line, or "" when it cannot be read.</summary>
        public static string TextOf(PartFeature f)
        {
            PlanarSketch sketch = SketchOf(f);
            if (sketch == null) return "";
            var parts = new List<string>();
            try
            {
                foreach (TextBox t in sketch.TextBoxes)
                {
                    string s = (t.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                    if (s.Length > 0) parts.Add(s);
                }
            }
            catch { }
            return string.Join(" / ", parts);
        }

        /// <summary>Faces that can carry text: flat ones, and round ones (tubes), where the text is wrapped.</summary>
        public static bool CanEngrave(Face f)
        {
            try { return f.SurfaceType == SurfaceTypeEnum.kPlaneSurface || f.SurfaceType == SurfaceTypeEnum.kCylinderSurface; }
            catch { return false; }
        }

        // ----- a new engraving ----------------------------------------------------------------------------------

        /// <summary>
        /// Creates the text sketch on the picked face, near the picked point, along the longest straight edge and
        /// readable from the current view. A previous engraving is suppressed (not deleted) until <see cref="Complete"/>,
        /// so cancelling brings it back.
        /// </summary>
        /// <param name="occ">The occurrence in the assembly, or null when the part itself is open.</param>
        /// <param name="point">The picked point in the space of the active document.</param>
        public static PlanarSketch Start(Inventor.Application app, PartDocument part, ComponentOccurrence occ, Face face,
                                         Point point, View view, string text, EngraveSettings settings, List<string> warnings)
        {
            PartComponentDefinition def = part.ComponentDefinition;
            Matrix toPart = occ != null ? occ.Transformation.Copy() : app.TransientGeometry.CreateMatrix();
            if (occ != null) toPart.Invert();
            Point partPoint = point.Copy();
            partPoint.TransformBy(toPart);

            DeleteSketches(def, SketchName + NewSuffix);
            DeletePlanes(def, PlaneName + NewSuffix);

            // the sketch first: suppressing the old engraving may change the picked face
            PlanarSketch sketch;
            if (face.SurfaceType == SurfaceTypeEnum.kCylinderSurface)
            {
                WorkPlane plane = TangentPlane(app, def, face, partPoint);
                plane.Name = PlaneName + NewSuffix;
                sketch = def.Sketches.Add(plane);
                try { plane.Visible = false; } catch { }
            }
            else sketch = def.Sketches.Add(face);
            sketch.Name = SketchName + NewSuffix;
            TextBox box = sketch.TextBoxes.AddFitted(sketch.ModelToSketchSpace(partPoint), text, Type.Missing);

            if (settings.TextHeightMm > 0 && !SetHeight(box, text, settings.TextHeightMm / 10, out string why))
                warnings.Add("Text height not set (" + why + ").");

            try { box.Rotation = Angle(app, view, toPart, sketch, face); }
            catch (Exception ex) { warnings.Add("Turning the text failed: " + ex.Message); }

            PartFeature old = Find(part);
            if (old != null) { try { old.Suppressed = true; } catch { } }
            return sketch;
        }

        /// <summary>
        /// A fixed work plane touching the round face at the picked point: X along the axis (so the text runs along the
        /// tube), the normal pointing out of the material.
        /// </summary>
        private static WorkPlane TangentPlane(Inventor.Application app, PartComponentDefinition def, Face face, Point p)
        {
            var cylinder = (Cylinder)face.Geometry;
            double[] points = { p.X, p.Y, p.Z };
            double[] normals = new double[3];
            face.Evaluator.GetNormalAtPoint(ref points, ref normals);
            TransientGeometry tg = app.TransientGeometry;
            Vector n = tg.CreateVector(normals[0], normals[1], normals[2]);
            Vector x = cylinder.AxisVector.AsVector();
            Vector y = n.CrossProduct(x);
            return def.WorkPlanes.AddFixed(p, x.AsUnitVector(), y.AsUnitVector(), false);
        }

        /// <summary>
        /// Engraves the sketch into the part (wrapped onto the face when the sketch is on a tangent work plane) and replaces
        /// the previous engraving. Returns false when the sketch holds no text, in which case nothing is engraved and the
        /// previous engraving is back.
        /// </summary>
        public static bool Complete(PartDocument part, PlanarSketch sketch, EngraveSettings settings, List<string> warnings)
        {
            if (sketch.TextBoxes.Count == 0)
            {
                Abandon(part, sketch);
                return false;
            }
            PartComponentDefinition def = part.ComponentDefinition;
            Profile profile = sketch.Profiles.AddForSolid();
            object wrap = WrapFace(def, sketch) ?? Type.Missing;
            double depth = Depth(part, settings);
            EmbossFeature feature;
            try
            {
                // negative: against the sketch normal, which points out of the material
                feature = def.Features.EmbossFeatures.AddEngraveFromFace(profile, depth,
                    PartFeatureExtentDirectionEnum.kNegativeExtentDirection, Type.Missing, wrap);
            }
            catch (Exception first)
            {
                Log.Write("Engrave from face (negative direction) failed, trying the other way: " + first.Message);
                feature = def.Features.EmbossFeatures.AddEngraveFromFace(profile, depth,
                    PartFeatureExtentDirectionEnum.kPositiveExtentDirection, Type.Missing, wrap);
            }

            // the engraving is in the part now; what follows only tidies up, and failures are reported, not fatal
            foreach (PartFeature f in Named(def, FeatureName))
            {
                try { f.Delete(false, false, false); } catch (Exception ex) { warnings.Add("Old engraving not removed: " + ex.Message); }
            }
            DeleteSketches(def, SketchName);
            DeletePlanes(def, PlaneName);
            try { feature.Name = FeatureName; } catch (Exception ex) { warnings.Add("Naming the engraving failed: " + ex.Message); }
            try { sketch.Name = SketchName; } catch (Exception ex) { warnings.Add("Naming the sketch failed: " + ex.Message); }
            foreach (WorkPlane wp in PlanesNamed(def, PlaneName + NewSuffix)) { try { wp.Name = PlaneName; } catch { } }
            SetEngravedProperty(part, true, warnings);
            try { part.Update2(true); } catch (Exception ex) { warnings.Add("Updating the part failed: " + ex.Message); }
            return true;
        }

        /// <summary>The round face under a sketch on a tangent work plane (found again at the plane's origin), else null.</summary>
        private static Face WrapFace(PartComponentDefinition def, PlanarSketch sketch)
        {
            try
            {
                if (!(sketch.PlanarEntity is WorkPlane plane)) return null;
                Point origin = ((Plane)plane.Plane).RootPoint;
                foreach (SurfaceBody body in def.SurfaceBodies)
                {
                    if (body.LocateUsingPoint(ObjectTypeEnum.kFaceObject, origin, 0.001) is Face f
                        && f.SurfaceType == SurfaceTypeEnum.kCylinderSurface) return f;
                }
            }
            catch (Exception ex) { Log.Write("Finding the face to wrap onto failed: " + ex.Message); }
            return null;
        }

        /// <summary>Removes a sketch made by <see cref="Start"/> (and its work plane) and brings the previous engraving back.</summary>
        public static void Abandon(PartDocument part, PlanarSketch sketch)
        {
            try { sketch?.Delete(); } catch { }
            try { DeletePlanes(part.ComponentDefinition, PlaneName + NewSuffix); } catch { }
            PartFeature old = Find(part);
            if (old != null) { try { old.Suppressed = false; } catch { } }
        }

        // ----- existing engravings ------------------------------------------------------------------------------

        /// <summary>After the sketch of an engraving was edited: an empty sketch deletes the engraving.</summary>
        public static void AfterEdit(PartDocument part, List<string> warnings)
        {
            PartFeature f = Find(part);
            if (f == null) return;
            PlanarSketch sketch = SketchOf(f);
            if (sketch != null && sketch.TextBoxes.Count == 0) Delete(part, warnings);
            else try { part.Update2(true); } catch (Exception ex) { warnings.Add("Updating the part failed: " + ex.Message); }
        }

        /// <summary>Deletes the engraving, its sketch and work plane, and clears the "Graveren" iProperty.</summary>
        public static void Delete(PartDocument part, List<string> warnings)
        {
            PartComponentDefinition def = part.ComponentDefinition;
            foreach (PartFeature f in Named(def, FeatureName)) f.Delete(false, false, false);
            DeleteSketches(def, SketchName);
            DeleteSketches(def, SketchName + NewSuffix);
            DeletePlanes(def, PlaneName);
            DeletePlanes(def, PlaneName + NewSuffix);
            SetEngravedProperty(part, false, warnings);
            try { part.Update2(true); } catch (Exception ex) { warnings.Add("Updating the part failed: " + ex.Message); }
        }

        // ----- helpers ------------------------------------------------------------------------------------------

        /// <summary>Depth in cm: the setting, but never more than a quarter of the sheet thickness.</summary>
        private static double Depth(PartDocument part, EngraveSettings settings)
        {
            double depth = settings.DepthMm / 10;
            if (part.ComponentDefinition is SheetMetalComponentDefinition sm)
            {
                try { depth = Math.Min(depth, Convert.ToDouble(sm.Thickness.Value) / 4); } catch { }
            }
            return depth;
        }

        private static List<PartFeature> Named(PartComponentDefinition def, string name)
        {
            var list = new List<PartFeature>();
            foreach (PartFeature f in def.Features)
                if (f.Name == name && (f is EmbossFeature || f is ExtrudeFeature)) list.Add(f);
            return list;
        }

        private static void DeleteSketches(PartComponentDefinition def, string name)
        {
            var list = new List<PlanarSketch>();
            foreach (PlanarSketch s in def.Sketches) if (s.Name == name) list.Add(s);
            foreach (PlanarSketch s in list) { try { s.Delete(); } catch { } }
        }

        private static List<WorkPlane> PlanesNamed(PartComponentDefinition def, string name)
        {
            var list = new List<WorkPlane>();
            foreach (WorkPlane p in def.WorkPlanes) if (p.Name == name) list.Add(p);
            return list;
        }

        private static void DeletePlanes(PartComponentDefinition def, string name)
        {
            foreach (WorkPlane p in PlanesNamed(def, name)) { try { p.Delete(false); } catch { } }
        }

        private static void SetEngravedProperty(PartDocument part, bool value, List<string> warnings)
        {
            try
            {
                PropertySet set = part.PropertySets["User Defined Properties"];
                Property p = null;
                try { p = set[EngravedProperty]; } catch { }
                if (p == null) set.Add(value, EngravedProperty);
                else p.Value = value;
            }
            catch (Exception ex) { warnings.Add("Setting the " + EngravedProperty + " iProperty failed: " + ex.Message); }
        }

        /// <summary>Text height (cm) through FormattedText; Inventor wants the decimal separator of the Windows locale.</summary>
        private static bool SetHeight(TextBox box, string text, double heightCm, out string why)
        {
            why = "";
            string local = heightCm.ToString(CultureInfo.CurrentCulture);
            string invariant = heightCm.ToString(CultureInfo.InvariantCulture);
            string escaped = System.Security.SecurityElement.Escape(text);
            foreach (string v in new[]
            {
                "<StyleOverride FontSize='" + local + "'>" + escaped + "</StyleOverride>",
                "<StyleOverride FontSize='" + invariant + "'>" + escaped + "</StyleOverride>"
            })
            {
                try { box.FormattedText = v; return true; }
                catch (Exception ex) { why = ex.Message; }
            }
            return false;
        }

        /// <summary>
        /// Text angle (0..2π, the only range Inventor accepts) along the longest straight edge of the face, choosing the
        /// one of its two directions that reads left to right on screen; falls back to the screen direction alone.
        /// </summary>
        private static double Angle(Inventor.Application app, View view, Matrix toPart, PlanarSketch sketch, Face face)
        {
            double screen = ScreenAngle(app, view, toPart, sketch);
            double edge = EdgeAngle(face, sketch);
            double h;
            if (double.IsNaN(edge)) h = double.IsNaN(screen) ? 0 : screen;
            else if (double.IsNaN(screen)) h = edge;
            else
            {
                double d = Math.Cos(edge - screen);
                if (Math.Abs(d) < 0.2)   // edge (nearly) square to "right" on screen: read upwards
                    h = Math.Cos(edge - (screen + Math.PI / 2)) >= 0 ? edge : edge + Math.PI;
                else
                    h = d >= 0 ? edge : edge + Math.PI;
            }
            if (double.IsNaN(h) || double.IsInfinity(h)) return 0;
            h %= 2 * Math.PI;
            return h < 0 ? h + 2 * Math.PI : h;
        }

        /// <summary>Sketch angle of "right" on screen, or of "up" minus 90° when the face is nearly edge-on; NaN if unknown.</summary>
        private static double ScreenAngle(Inventor.Application app, View view, Matrix toPart, PlanarSketch sketch)
        {
            try
            {
                if (view == null) return double.NaN;
                Camera cam = view.Camera;
                Vector toEye = cam.Target.VectorTo(cam.Eye);
                Vector up = cam.UpVector.AsVector();
                Vector right = up.CrossProduct(toEye);
                right.TransformBy(toPart);
                up.TransformBy(toPart);

                TransientGeometry tg = app.TransientGeometry;
                Point o = sketch.SketchToModelSpace(tg.CreatePoint2d(0, 0));
                Vector ax = o.VectorTo(sketch.SketchToModelSpace(tg.CreatePoint2d(1, 0)));
                Vector ay = o.VectorTo(sketch.SketchToModelSpace(tg.CreatePoint2d(0, 1)));

                double rx = right.DotProduct(ax), ry = right.DotProduct(ay);
                if (Math.Sqrt(rx * rx + ry * ry) > 0.3 * right.Length) return Math.Atan2(ry, rx);
                double ux = up.DotProduct(ax), uy = up.DotProduct(ay);
                if (Math.Sqrt(ux * ux + uy * uy) > 0.3 * up.Length) return Math.Atan2(uy, ux) - Math.PI / 2;
            }
            catch { }
            return double.NaN;
        }

        /// <summary>Sketch angle of the longest straight edge of the face; NaN if it has none.</summary>
        private static double EdgeAngle(Face face, PlanarSketch sketch)
        {
            double best = 0, angle = double.NaN;
            foreach (Edge e in face.Edges)
            {
                if (e.GeometryType != CurveTypeEnum.kLineSegmentCurve) continue;
                Point2d p1 = sketch.ModelToSketchSpace(e.StartVertex.Point);
                Point2d p2 = sketch.ModelToSketchSpace(e.StopVertex.Point);
                double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > best) { best = len; angle = Math.Atan2(dy, dx); }
            }
            return angle;
        }
    }
}
