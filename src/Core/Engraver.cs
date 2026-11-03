using System;
using System.Collections.Generic;
using System.Globalization;
using Inventor;

namespace Engrave.Core
{
    /// <summary>
    /// Puts the text as a sketch on a face, cuts it shallowly into the part, and finds, edits or deletes engravings made
    /// earlier. The names are those of the iLogic rule this add-in replaces, so engravings made by the rule are found too.
    /// </summary>
    internal static class Engraver
    {
        public const string FeatureName = "Graveer";
        public const string SketchName = "GraveerSketch";
        private const string NewSketchName = "GraveerSketch (new)";
        private const string EngravedProperty = "Graveren";

        // ----- finding ------------------------------------------------------------------------------------------

        /// <summary>The engraving cut of the part, or null when it has none.</summary>
        public static ExtrudeFeature Find(PartDocument part)
        {
            try
            {
                foreach (ExtrudeFeature f in part.ComponentDefinition.Features.ExtrudeFeatures)
                    if (f.Name == FeatureName) return f;
            }
            catch { /* part not readable */ }
            return null;
        }

        public static PlanarSketch SketchOf(ExtrudeFeature f)
        {
            try { return f.Profile.Parent as PlanarSketch; } catch { return null; }
        }

        /// <summary>The engraved text as one line, or "" when it cannot be read.</summary>
        public static string TextOf(ExtrudeFeature f)
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

            ExtrudeFeature old = Find(part);
            if (old != null) { try { old.Suppressed = true; } catch { } }
            DeleteSketches(def, NewSketchName);

            PlanarSketch sketch = def.Sketches.Add(face);
            sketch.Name = NewSketchName;
            TextBox box = sketch.TextBoxes.AddFitted(sketch.ModelToSketchSpace(partPoint), text, Type.Missing);

            if (settings.TextHeightMm > 0 && !SetHeight(box, text, settings.TextHeightMm / 10, out string why))
                warnings.Add("Text height not set (" + why + ").");

            try { box.Rotation = Angle(app, view, toPart, sketch, face); }
            catch (Exception ex) { warnings.Add("Turning the text failed: " + ex.Message); }
            return sketch;
        }

        /// <summary>
        /// Cuts the sketch into the part and replaces the previous engraving. Returns false when the sketch holds no text,
        /// in which case nothing is cut and the previous engraving is back.
        /// </summary>
        public static bool Complete(PartDocument part, PlanarSketch sketch, EngraveSettings settings, List<string> warnings)
        {
            if (sketch.TextBoxes.Count == 0)
            {
                Abandon(part, sketch);
                return false;
            }
            PartComponentDefinition def = part.ComponentDefinition;
            ExtrudeFeatures extrudes = def.Features.ExtrudeFeatures;
            Profile profile = sketch.Profiles.AddForSolid();
            ExtrudeDefinition cut = extrudes.CreateExtrudeDefinition(profile, PartFeatureOperationEnum.kCutOperation);
            // negative direction: into the material, the sketch lies on the picked face
            cut.SetDistanceExtent(Depth(part, settings), PartFeatureExtentDirectionEnum.kNegativeExtentDirection);
            ExtrudeFeature feature = extrudes.Add(cut);

            // the engraving is in the part now; what follows only tidies up, and failures are reported, not fatal
            foreach (ExtrudeFeature f in Named(def, FeatureName))
            {
                try { f.Delete(false, false, false); } catch (Exception ex) { warnings.Add("Old engraving not removed: " + ex.Message); }
            }
            DeleteSketches(def, SketchName);
            try { feature.Name = FeatureName; } catch (Exception ex) { warnings.Add("Naming the cut failed: " + ex.Message); }
            try { sketch.Name = SketchName; } catch (Exception ex) { warnings.Add("Naming the sketch failed: " + ex.Message); }
            SetEngravedProperty(part, true, warnings);
            try { part.Update2(true); } catch (Exception ex) { warnings.Add("Updating the part failed: " + ex.Message); }
            return true;
        }

        /// <summary>Removes a sketch made by <see cref="Start"/> and brings the previous engraving back.</summary>
        public static void Abandon(PartDocument part, PlanarSketch sketch)
        {
            try { sketch?.Delete(); } catch { }
            ExtrudeFeature old = Find(part);
            if (old != null) { try { old.Suppressed = false; } catch { } }
        }

        // ----- existing engravings ------------------------------------------------------------------------------

        /// <summary>After the sketch of an engraving was edited: an empty sketch deletes the engraving.</summary>
        public static void AfterEdit(PartDocument part, List<string> warnings)
        {
            ExtrudeFeature f = Find(part);
            if (f == null) return;
            PlanarSketch sketch = SketchOf(f);
            if (sketch != null && sketch.TextBoxes.Count == 0) Delete(part, warnings);
            else try { part.Update2(true); } catch (Exception ex) { warnings.Add("Updating the part failed: " + ex.Message); }
        }

        /// <summary>Deletes the engraving cut and its sketch, and clears the "Graveren" iProperty.</summary>
        public static void Delete(PartDocument part, List<string> warnings)
        {
            PartComponentDefinition def = part.ComponentDefinition;
            foreach (ExtrudeFeature f in Named(def, FeatureName)) f.Delete(false, false, false);
            DeleteSketches(def, SketchName);
            DeleteSketches(def, NewSketchName);
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

        private static List<ExtrudeFeature> Named(PartComponentDefinition def, string name)
        {
            var list = new List<ExtrudeFeature>();
            foreach (ExtrudeFeature f in def.Features.ExtrudeFeatures) if (f.Name == name) list.Add(f);
            return list;
        }

        private static void DeleteSketches(PartComponentDefinition def, string name)
        {
            var list = new List<PlanarSketch>();
            foreach (PlanarSketch s in def.Sketches) if (s.Name == name) list.Add(s);
            foreach (PlanarSketch s in list) { try { s.Delete(); } catch { } }
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
