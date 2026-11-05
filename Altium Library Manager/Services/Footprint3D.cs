using System.Text.Json.Serialization;
using OriginalCircuit.AltiumSharp;
using OriginalCircuit.AltiumSharp.BasicTypes;
using OriginalCircuit.AltiumSharp.Records;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// Footprint data for the 3D viewer. All sizes are in millimeters, angles in degrees.
    /// Serialized to JSON and passed to Viewer3D/viewer.html.
    /// </summary>
    public record Footprint3D(string Name, IReadOnlyList<Pad3D> Pads, IReadOnlyList<Body3D> Bodies)
    {
        public bool HasBodies => Bodies.Count > 0;

        public static Footprint3D From(PcbComponent component)
        {
            var pads = component.GetPrimitivesOfType<PcbPad>(true)
                .Where(p => !IsBottom(p.Layer))
                .Select(p => new Pad3D(
                    Mm(p.Location.X), Mm(p.Location.Y),
                    Mm(p.SizeTop.X), Mm(p.SizeTop.Y),
                    p.Rotation,
                    p.ShapeTop == PcbPadShape.Round,
                    p.HasHole ? Mm(p.HoleSize) : 0))
                .ToList();

            var bodies = component.GetPrimitivesOfType<PcbComponentBody>(true)
                .Select(b => new Body3D(
                    string.IsNullOrEmpty(b.StepModel) ? null : ModelKey(b),
                    b.StepModel,
                    Mm(b.Model2DLocation.X), Mm(b.Model2DLocation.Y), Mm(b.Model3DDz),
                    b.Model3DRotX, b.Model3DRotY, b.Model3DRotZ, b.Model2DRotation,
                    b.Outline?.Select(pt => new[] { Mm(pt.X), Mm(pt.Y) }).ToList() ?? [],
                    Mm(b.StandOffHeight), Mm(b.OverallHeight),
                    $"#{b.BodyColor3D.R:X2}{b.BodyColor3D.G:X2}{b.BodyColor3D.B:X2}",
                    b.BodyOpacity3D <= 0 ? 1 : b.BodyOpacity3D,
                    IsBottom(b.Layer) || b.V7Layer?.Contains("BOTTOM", StringComparison.OrdinalIgnoreCase) == true))
                // A body with neither STEP nor an outline has nothing to draw
                .Where(b => !string.IsNullOrEmpty(b.Step) || b.Outline.Count >= 3)
                .ToList();

            return new Footprint3D(((IComponent)component).Name, pads, bodies);
        }

        /// <summary>
        /// Stable, URL-safe identity of an embedded model: Altium's model id plus its checksum and size, so the viewer
        /// can reuse an already tessellated model and the STEP text is transferred only once.
        /// </summary>
        private static string ModelKey(PcbComponentBody body)
        {
            var id = new string((body.ModelId ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
            return $"{(id.Length > 0 ? id : "model")}-{(uint)body.ModelChecksum:x8}-{body.StepModel.Length}";
        }

        private static double Mm(Coord c) => Math.Round(c.ToMMs(), 4);

        private static bool IsBottom(Layer layer) =>
            layer.Name?.Contains("Bottom", StringComparison.OrdinalIgnoreCase) == true;
    }

    public record Pad3D(double X, double Y, double Width, double Height, double Rotation, bool Round, double Hole);

    /// <summary>
    /// A component body: either an embedded STEP model (<see cref="Step"/>) or a prism extruded from
    /// <see cref="Outline"/> between <see cref="StandOff"/> and <see cref="Height"/>.
    /// The STEP text (often megabytes) isn't serialized: the viewer downloads it by <see cref="ModelKey"/> only when
    /// it doesn't have the model yet.
    /// </summary>
    public record Body3D(
        string? ModelKey, [property: JsonIgnore] string? Step,
        double X, double Y, double Z,
        double RotX, double RotY, double RotZ, double Rotation2D,
        IReadOnlyList<double[]> Outline, double StandOff, double Height,
        string Color, double Opacity, bool Bottom);
}
