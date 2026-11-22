using System.Globalization;

namespace SkillSync.Daemon.Desktop;

/// <summary>
/// The single definition of the SkillHub mark: a hub with three skill nodes on a sync orbit, on an indigo-to-violet
/// tile. <see cref="Svg"/> is written to <c>wwwroot/img/skillhub-logo.svg</c> (header, favicon, welcome guide) and
/// <c>docs/brand/skillhub-logo.svg</c> (README); <see cref="SkillHubIconRenderer"/> draws the same geometry for the
/// window, taskbar, tray and installer icons. Coordinates are in a 64-unit square.
/// </summary>
public static class SkillHubBrand
{
    public const float ViewBox = 64f;
    public const float TileInset = 2f;
    public const float TileRadius = 15f;
    public const string GradientStart = "#4338CA";
    public const string GradientEnd = "#6D28D9";
    public const float Center = 32f;
    public const float OrbitRadius = 18f;
    public const float OrbitStroke = 3f;
    public const float OrbitOpacity = 0.42f;
    public const float SpokeStroke = 4f;
    public const float HubRadius = 8f;
    public const float NodeRadius = 6f;

    /// <summary>Node angles in degrees, clockwise from the positive x axis (screen coordinates): top, lower right, lower left.</summary>
    public static readonly float[] NodeAngles = [-90f, 30f, 150f];

    public static (float X, float Y) Node(int index)
    {
        var radians = NodeAngles[index] * MathF.PI / 180f;
        return (Center + OrbitRadius * MathF.Cos(radians), Center + OrbitRadius * MathF.Sin(radians));
    }

    public static string Svg()
    {
        static string F(float value) => MathF.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
        var nodes = Enumerable.Range(0, NodeAngles.Length).Select(Node).ToArray();
        var spokes = string.Concat(nodes.Select(node => $"M{F(Center)} {F(Center)}L{F(node.X)} {F(node.Y)}"));
        var size = ViewBox - TileInset * 2;
        return
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\" role=\"img\" aria-label=\"SkillHub\">\n" +
            "  <defs><linearGradient id=\"skillhub-tile\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">" +
            $"<stop offset=\"0\" stop-color=\"{GradientStart}\"/><stop offset=\"1\" stop-color=\"{GradientEnd}\"/></linearGradient></defs>\n" +
            $"  <rect x=\"{F(TileInset)}\" y=\"{F(TileInset)}\" width=\"{F(size)}\" height=\"{F(size)}\" rx=\"{F(TileRadius)}\" fill=\"url(#skillhub-tile)\"/>\n" +
            $"  <circle cx=\"{F(Center)}\" cy=\"{F(Center)}\" r=\"{F(OrbitRadius)}\" fill=\"none\" stroke=\"#fff\" stroke-opacity=\"{F(OrbitOpacity)}\" stroke-width=\"{F(OrbitStroke)}\"/>\n" +
            $"  <path d=\"{spokes}\" stroke=\"#fff\" stroke-width=\"{F(SpokeStroke)}\" stroke-linecap=\"round\"/>\n" +
            $"  <circle cx=\"{F(Center)}\" cy=\"{F(Center)}\" r=\"{F(HubRadius)}\" fill=\"#fff\"/>\n" +
            string.Concat(nodes.Select(node => $"  <circle cx=\"{F(node.X)}\" cy=\"{F(node.Y)}\" r=\"{F(NodeRadius)}\" fill=\"#fff\"/>\n")) +
            "</svg>\n";
    }
}
