using System.Globalization;
using System.Text;
using NetVips;

namespace Commonage.Web.Demo;

/// <summary>
/// Simple drawn pictures for the demo archive. They are made here, so the demo contains nothing anyone else has
/// rights to, and they have no text, so they need no fonts on the server.
/// </summary>
public static class DemoImages
{
    public const int Width = 1600;
    public const int Height = 1100;

    public static readonly IReadOnlyList<string> Scenes = ["mill-winter", "flood", "bakery", "bridge-party", "team", "school", "tram", "grandmother-shop"];

    /// <summary>Renders a scene as a JPEG, in a slightly faded, warm tone like an old print.</summary>
    public static byte[] Render(string scene)
    {
        var svg = Svg(scene);
        using var drawing = Image.NewFromBuffer(Encoding.UTF8.GetBytes(svg));
        using var flat = drawing.Flatten(background: [255, 255, 255]);
        using var aged = flat.Linear([0.82, 0.78, 0.68], [38, 34, 28]);
        using var output = aged.Cast(Enums.BandFormat.Uchar);
        return output.JpegsaveBuffer(q: 88, keep: Enums.ForeignKeep.None);
    }

    internal static string Svg(string scene)
    {
        var body = scene switch
        {
            "mill-winter" => Sky("#c9d3dc", "#eef1f4") + Ground("#f4f6f8", 760) + House(300, 420, 520, 340, "#8b6f5a", "#5d4a3c")
                + Wheel(860, 640, 190) + Snowflakes() + Trees(1150, 700, "#7d8a80"),
            "flood" => Sky("#9fb3c4", "#d7dfe6") + House(80, 380, 420, 380, "#c4a27c", "#7b5b45") + House(560, 340, 460, 420, "#a9b7a0", "#5f6b57")
                + House(1080, 400, 440, 360, "#d2b48c", "#8a6a4f") + Water(700) + Boat(620, 790),
            "bakery" => Sky("#d8c7a8", "#f1e7d3") + Ground("#9c9184", 900) + ShopFront(260, 300, 1080, 600, "#7a4b3a", "#e9d8b8") + People(220, 870, 3),
            "bridge-party" => Sky("#a9c6de", "#e6eef5") + Water(820) + Bridge() + Bunting(200, 1400, 380) + People(250, 700, 9),
            "team" => Sky("#b9cfe0", "#e9f0f5") + Ground("#7fa25f", 620) + Goal(1200, 430) + TeamRows(),
            "school" => Sky("#c3d4e2", "#eef3f7") + Ground("#a39b8f", 840) + School() + People(300, 930, 6),
            "tram" => Sky("#c8cfd6", "#eceff2") + Ground("#8f8a83", 820) + Rails() + Tram() + House(80, 300, 340, 520, "#b49a7c", "#6d5845")
                + House(1220, 280, 320, 540, "#9fa89a", "#5b6356"),
            "grandmother-shop" => Sky("#d9cdb6", "#f3ecdf") + Ground("#a8a091", 900) + ShopFront(360, 340, 880, 560, "#3f5c4f", "#efe5cf") + People(460, 880, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(scene), scene, "Unknown demo scene."),
        };
        return $"""<svg xmlns="http://www.w3.org/2000/svg" width="{Width}" height="{Height}" viewBox="0 0 {Width} {Height}">{body}</svg>""";
    }

    private static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Sky(string top, string bottom) =>
        $"""<defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/></linearGradient></defs><rect width="{Width}" height="{Height}" fill="url(#sky)"/>""";

    private static string Ground(string color, int top) => $"""<rect x="0" y="{top}" width="{Width}" height="{Height - top}" fill="{color}"/>""";

    private static string House(int x, int y, int w, int h, string wall, string roof)
    {
        var sb = new StringBuilder();
        sb.Append($"""<polygon points="{x - 30},{y} {x + w / 2},{y - h / 3} {x + w + 30},{y}" fill="{roof}"/>""");
        sb.Append($"""<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{wall}"/>""");
        for (var row = 0; row < 2; row++)
            for (var col = 0; col < 3; col++)
                sb.Append($"""<rect x="{x + 40 + col * (w - 80) / 3}" y="{y + 40 + row * h / 3}" width="{(w - 160) / 3}" height="{h / 5}" fill="#3e4a52" opacity="0.75"/>""");
        sb.Append($"""<rect x="{x + w / 2 - 35}" y="{y + h - 140}" width="70" height="140" fill="#4b3a2f"/>""");
        return sb.ToString();
    }

    private static string Wheel(int cx, int cy, int r)
    {
        var sb = new StringBuilder($"""<circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="#4a3b30" stroke-width="22"/>""");
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6;
            sb.Append($"""<line x1="{cx}" y1="{cy}" x2="{F(cx + r * Math.Cos(a))}" y2="{F(cy + r * Math.Sin(a))}" stroke="#4a3b30" stroke-width="10"/>""");
        }
        return sb.ToString();
    }

    private static string Snowflakes()
    {
        var random = new Random(7);
        var sb = new StringBuilder();
        for (var i = 0; i < 140; i++)
            sb.Append($"""<circle cx="{random.Next(Width)}" cy="{random.Next(Height)}" r="{random.Next(3, 8)}" fill="#fff" opacity="0.85"/>""");
        return sb.ToString();
    }

    private static string Trees(int x, int y, string color)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 4; i++)
            sb.Append($"""<polygon points="{x + i * 100},{y} {x + 50 + i * 100},{y - 220 - i % 2 * 60} {x + 100 + i * 100},{y}" fill="{color}"/>""");
        return sb.ToString();
    }

    private static string Water(int top)
    {
        var sb = new StringBuilder($"""<rect x="0" y="{top}" width="{Width}" height="{Height - top}" fill="#6f8ea3" opacity="0.92"/>""");
        for (var y = top + 30; y < Height; y += 45)
            sb.Append($"""<path d="M0 {y} Q 200 {y - 14} 400 {y} T 800 {y} T 1200 {y} T 1600 {y}" stroke="#a9c0cf" stroke-width="5" fill="none"/>""");
        return sb.ToString();
    }

    private static string Boat(int x, int y) =>
        $"""<path d="M{x} {y} L{x + 260} {y} L{x + 220} {y + 60} L{x + 40} {y + 60} Z" fill="#5a4636"/><circle cx="{x + 110}" cy="{y - 40}" r="26" fill="#3c3530"/><rect x="{x + 90}" y="{y - 16}" width="40" height="20" fill="#3c3530"/>""";

    private static string ShopFront(int x, int y, int w, int h, string frame, string window)
    {
        var sb = new StringBuilder($"""<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{frame}"/>""");
        sb.Append($"""<rect x="{x}" y="{y}" width="{w}" height="90" fill="#2f2a26"/>""");
        sb.Append($"""<rect x="{x + 50}" y="{y + 140}" width="{w / 2 - 100}" height="{h - 220}" fill="{window}"/>""");
        sb.Append($"""<rect x="{x + w / 2 + 50}" y="{y + 140}" width="{w / 2 - 100}" height="{h - 220}" fill="{window}"/>""");
        for (var i = 0; i < 4; i++)
            sb.Append($"""<circle cx="{x + 120 + i * 90}" cy="{y + h - 140}" r="32" fill="#b8874f"/>""");
        sb.Append($"""<path d="M{x - 20} {y + 90} L{x + w + 20} {y + 90} L{x + w - 20} {y + 150} L{x + 20} {y + 150} Z" fill="#a8463a"/>""");
        return sb.ToString();
    }

    private static string People(int x, int y, int count)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var px = x + i * 120;
            var tall = 150 + i * 37 % 50;
            sb.Append($"""<circle cx="{px}" cy="{y - tall - 26}" r="26" fill="#3a332e"/><rect x="{px - 30}" y="{y - tall}" width="60" height="{tall}" rx="22" fill="#3a332e"/>""");
        }
        return sb.ToString();
    }

    private static string Bridge() =>
        """<path d="M0 760 L1600 760 L1600 820 L0 820 Z" fill="#8c7f72"/><path d="M200 820 Q 500 600 800 820 Q 1100 600 1400 820" fill="none" stroke="#6d6156" stroke-width="40"/>""";

    private static string Bunting(int x1, int x2, int y)
    {
        var sb = new StringBuilder($"""<path d="M{x1} {y} Q {(x1 + x2) / 2} {y + 120} {x2} {y}" stroke="#4b4038" stroke-width="4" fill="none"/>""");
        string[] colors = ["#b5523b", "#d6a447", "#4f7a96", "#6c8f5a"];
        for (var i = 0; i < 16; i++)
        {
            var t = (i + 0.5) / 16;
            var fx = x1 + (x2 - x1) * t;
            var fy = y + 240 * t * (1 - t); // the curve of the string
            sb.Append($"""<polygon points="{F(fx - 22)},{F(fy)} {F(fx + 22)},{F(fy)} {F(fx)},{F(fy + 50)}" fill="{colors[i % 4]}"/>""");
        }
        return sb.ToString();
    }

    private static string Goal(int x, int y) =>
        $"""<rect x="{x}" y="{y}" width="300" height="190" fill="none" stroke="#f3f3f0" stroke-width="14"/>""";

    private static string TeamRows()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 6; i++)
            sb.Append($"""<circle cx="{330 + i * 150}" cy="{560}" r="34" fill="#3a332e"/><rect x="{290 + i * 150}" y="600" width="80" height="190" rx="26" fill="#f1efe8"/><rect x="{290 + i * 150}" y="700" width="80" height="90" fill="#2d3b55"/>""");
        for (var i = 0; i < 5; i++)
            sb.Append($"""<circle cx="{400 + i * 150}" cy="{760}" r="34" fill="#3a332e"/><rect x="{360 + i * 150}" y="800" width="80" height="140" rx="26" fill="#f1efe8"/>""");
        return sb.ToString();
    }

    private static string School()
    {
        var sb = new StringBuilder("""<rect x="240" y="330" width="1120" height="510" fill="#b9775b"/><polygon points="200,330 800,170 1400,330" fill="#5e4a42"/>""");
        for (var row = 0; row < 2; row++)
            for (var col = 0; col < 7; col++)
                sb.Append($"""<rect x="{300 + col * 150}" y="{390 + row * 200}" width="90" height="130" fill="#e8e2d2"/>""");
        sb.Append("""<rect x="740" y="690" width="120" height="150" fill="#3f342d"/><circle cx="800" cy="260" r="40" fill="#efe9da"/>""");
        return sb.ToString();
    }

    private static string Rails() =>
        """<line x1="0" y1="900" x2="1600" y2="900" stroke="#5a554f" stroke-width="10"/><line x1="0" y1="960" x2="1600" y2="960" stroke="#5a554f" stroke-width="10"/><line x1="0" y1="250" x2="1600" y2="250" stroke="#3d3a36" stroke-width="5"/>""";

    private static string Tram()
    {
        var sb = new StringBuilder("""<rect x="430" y="470" width="760" height="410" rx="40" fill="#c99a3d"/><rect x="430" y="780" width="760" height="100" fill="#8f3d2f"/>""");
        for (var i = 0; i < 5; i++)
            sb.Append($"""<rect x="{470 + i * 145}" y="520" width="110" height="170" fill="#e6e2d6"/>""");
        sb.Append("""<line x1="810" y1="470" x2="900" y2="250" stroke="#3d3a36" stroke-width="8"/><circle cx="560" cy="900" r="40" fill="#2f2c29"/><circle cx="1060" cy="900" r="40" fill="#2f2c29"/>""");
        return sb.ToString();
    }
}
