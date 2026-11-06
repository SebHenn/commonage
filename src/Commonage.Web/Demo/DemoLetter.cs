using System.Globalization;
using System.Text;
using NetVips;

namespace Commonage.Web.Demo;

/// <summary>
/// A handwritten letter for the demo archive, to try out reading a scan. Like the other demo pictures it is drawn here, stroke
/// by stroke, so it contains nothing anyone else has rights to and needs no fonts on the server.
/// </summary>
public static class DemoLetter
{
    public const int Width = 2400;
    public const int Height = 3400;

    private const double Scale = 8.4;
    private const int Margin = 230;
    private const int LineHeight = 172;

    /// <summary>The words of the letter, page by page. The item's description repeats them, as a curator would write them out.</summary>
    public static string[][] Pages(bool german) => german
        ?
        [
            ["Lindenau, 14. März 1964", "Liebe Mitglieder,", "der Gemeinderat hat zugestimmt, dass der Verein die Wiese am Bach als neuen Sportplatz nutzen darf. Das haben wir lange erhofft.", "Wir brauchen samstags Helfer, um den Boden zu ebnen. Bitte Spaten mitbringen. Für Suppe ist gesorgt."],
            ["Beginn ist der 4. April um neun Uhr, am Tor beim Bach.", "Bitte melden sich bis Ende März bei mir: alle, die einen Wagen oder ein Pferd leihen können.", "Mit sportlichem Gruß", "Hans Albrecht, Vorsitzender"],
        ]
        :
        [
            ["Lindenau, 14 March 1964", "Dear members,", "the town council has agreed that the club may use the meadow by the brook as its new pitch. We have hoped for this for a long time.", "We need helpers on Saturdays to level the ground. Bring a spade. There will be soup."],
            ["We start on the 4th of April at nine o'clock, at the gate by the brook.", "Anyone who can lend a cart or a horse, please tell me by the end of March.", "With sporting greetings,", "Hans Albrecht, Chairman"],
        ];

    /// <summary>A character without a drawing would just be missing from the page.</summary>
    public static bool CanWrite(string text) => text.All(Glyphs.ContainsKey);

    /// <summary>Renders a page as a JPEG, as if scanned: warm paper, a fold, and blue ink.</summary>
    public static byte[] Render(IEnumerable<string> paragraphs, int seed)
    {
        using var drawing = Image.NewFromBuffer(Encoding.UTF8.GetBytes(Svg(paragraphs, seed)));
        using var flat = drawing.Flatten(background: [255, 255, 255]);
        return flat.JpegsaveBuffer(q: 88, keep: Enums.ForeignKeep.None);
    }

    internal static string Svg(IEnumerable<string> paragraphs, int seed)
    {
        var random = new Random(seed);
        var ink = new StringBuilder();
        double y = Margin + 40;
        foreach (var paragraph in paragraphs)
        {
            foreach (var line in Wrap(paragraph))
            {
                WriteLine(ink, line, Margin, y, random);
                y += LineHeight;
            }
            y += LineHeight / 3.0;
        }

        return $"""<svg xmlns="http://www.w3.org/2000/svg" width="{Width}" height="{Height}" viewBox="0 0 {Width} {Height}"><rect width="{Width}" height="{Height}" fill="#efe6d2"/><rect y="{Height / 2 - 3}" width="{Width}" height="6" fill="#e3d9c4"/><g fill="none" stroke="#22305f" stroke-width="{F(0.5)}" stroke-linecap="round" stroke-linejoin="round">{ink}</g></svg>""";
    }

    private static IEnumerable<string> Wrap(string paragraph)
    {
        var line = new StringBuilder();
        foreach (var word in paragraph.Split(' '))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (WidthOf(candidate) * Scale > Width - (2 * Margin) && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear();
                line.Append(word);
            }
            else
            {
                line.Clear();
                line.Append(candidate);
            }
        }
        yield return line.ToString();
    }

    private static double WidthOf(string text) => text.Sum(c => Glyph(c).Width + Spacing);

    private const double Spacing = 0.9;

    private static void WriteLine(StringBuilder ink, string line, double x, double y, Random random)
    {
        // A hand does not keep to the line, and each letter comes out a little differently.
        var drift = (random.NextDouble() - 0.5) * 12;
        foreach (var character in line)
        {
            var (path, width) = Glyph(character);
            if (path.Length > 0)
            {
                var wobble = (random.NextDouble() - 0.5) * 3;
                var size = Scale * (0.97 + (random.NextDouble() * 0.06));
                ink.Append(CultureInfo.InvariantCulture, $"""<path transform="translate({F(x)} {F(y + drift + wobble)}) skewX(-7) scale({F(size)})" d="{path}"/>""");
            }
            x += (width + Spacing) * Scale;
            drift += (random.NextDouble() - 0.5) * 0.9;
        }
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static (string Path, double Width) Glyph(char character) =>
        Glyphs.TryGetValue(character, out var glyph) ? glyph : Glyphs.GetValueOrDefault(' ');

    private const string Dots = " M1.8 2.7 L1.8 2.9 M3.8 2.7 L3.8 2.9";
    private const string A = "M5 6 Q4 4.5 2.7 4.5 Q.5 4.5 .5 7.3 Q.5 10 2.7 10 Q4.2 10 5 8.3 M5 4.6 L5 10";
    private const string O = "M2.8 4.5 Q.5 4.5 .5 7.2 Q.5 10 2.8 10 Q5.1 10 5.1 7.2 Q5.1 4.5 2.8 4.5";
    private const string U = "M.7 4.6 L.7 8 Q.7 10 2.7 10 Q4.2 10 5 8.4 M5 4.6 L5 10";

    // Drawn on a grid: the baseline at 10, small letters from 4.5, capitals and tall letters from about 0, tails to 13.
    private static readonly Dictionary<char, (string Path, double Width)> Glyphs = new()
    {
        [' '] = ("", 3),
        ['a'] = (A, 5.8), ['ä'] = (A + Dots, 5.8),
        ['b'] = ("M.7 0 L.7 10 M.7 6.2 Q1.6 4.5 3 4.5 Q5.3 4.5 5.3 7.2 Q5.3 10 3 10 Q1.6 10 .7 8.3", 5.8),
        ['c'] = ("M4.8 5.6 Q4 4.5 2.8 4.5 Q.5 4.5 .5 7.2 Q.5 10 2.8 10 Q4 10 4.8 8.9", 5.2),
        ['d'] = ("M5 0 L5 10 M5 6.2 Q4.1 4.5 2.7 4.5 Q.5 4.5 .5 7.2 Q.5 10 2.7 10 Q4.1 10 5 8.3", 5.8),
        ['e'] = ("M.6 7.2 L5 7.2 Q5 4.5 2.8 4.5 Q.5 4.5 .5 7.2 Q.5 10 2.8 10 Q4.2 10 4.9 9", 5.4),
        ['f'] = ("M4 .3 Q2 -.2 2 2 L2 10 M.5 4.7 L3.7 4.7", 4),
        ['g'] = ("M5 6.2 Q4.1 4.5 2.7 4.5 Q.5 4.5 .5 7.2 Q.5 10 2.7 10 Q4.1 10 5 8.3 M5 4.6 L5 11 Q5 13.2 2.7 13.2 Q1.3 13.2 .7 12.4", 5.8),
        ['h'] = ("M.7 0 L.7 10 M.7 6.2 Q1.6 4.5 3 4.5 Q5 4.5 5 6.5 L5 10", 5.6),
        ['i'] = ("M1 4.6 L1 10 M1 2.4 L1 2.6", 1.8),
        ['j'] = ("M1.6 4.6 L1.6 11.5 Q1.6 13.2 0 13 M1.6 2.4 L1.6 2.6", 2.4),
        ['k'] = ("M.7 0 L.7 10 M4.6 4.6 L.7 8 M2 6.9 L4.9 10", 5),
        ['l'] = ("M1 0 L1 10", 1.8),
        ['m'] = ("M.7 4.6 L.7 10 M.7 6.2 Q1.4 4.5 2.7 4.5 Q4.2 4.5 4.2 6.4 L4.2 10 M4.2 6.2 Q5 4.5 6.2 4.5 Q7.7 4.5 7.7 6.4 L7.7 10", 8.3),
        ['n'] = ("M.7 4.6 L.7 10 M.7 6.2 Q1.6 4.5 3 4.5 Q5 4.5 5 6.5 L5 10", 5.6),
        ['o'] = (O, 5.4), ['ö'] = (O + Dots, 5.4),
        ['p'] = ("M.7 4.6 L.7 13.2 M.7 6.2 Q1.6 4.5 3 4.5 Q5.3 4.5 5.3 7.2 Q5.3 10 3 10 Q1.6 10 .7 8.3", 5.8),
        ['q'] = ("M5 4.6 L5 13.2 M5 6.2 Q4.1 4.5 2.7 4.5 Q.5 4.5 .5 7.2 Q.5 10 2.7 10 Q4.1 10 5 8.3", 5.8),
        ['r'] = ("M.7 4.6 L.7 10 M.7 6.6 Q1.6 4.5 3.6 4.7", 3.7),
        ['s'] = ("M4.3 5.4 Q3.6 4.5 2.5 4.5 Q.7 4.5 .7 5.9 Q.7 7 2.5 7.3 Q4.6 7.7 4.6 8.7 Q4.6 10 2.6 10 Q1.2 10 .5 9", 4.9),
        ['t'] = ("M2 1.8 L2 8.6 Q2 10 3.7 9.8 M.5 4.7 L3.7 4.7", 4),
        ['u'] = (U, 5.6), ['ü'] = (U + Dots, 5.6),
        ['v'] = ("M.4 4.6 L2.6 10 L4.8 4.6", 5),
        ['w'] = ("M.4 4.6 L2 10 L3.7 5.5 L5.4 10 L7 4.6", 7.2),
        ['x'] = ("M.5 4.6 L4.5 10 M4.5 4.6 L.5 10", 4.8),
        ['y'] = ("M.4 4.6 L2.6 10 M4.8 4.6 L2.2 11.5 Q1.7 13 .4 13", 5),
        ['z'] = ("M.6 4.6 L4.5 4.6 L.6 10 L4.6 10", 5),
        ['ß'] = ("M.7 13 L.7 2.3 Q.7 .2 2.8 .2 Q4.6 .2 4.6 2 Q4.6 3.7 2.6 4.2 Q5.4 4.7 5.4 7.2 Q5.4 10 3 10 Q2 10 1.4 9.6", 5.8),
        ['A'] = ("M.4 10 L3.4 .5 L6.4 10 M1.5 6.8 L5.3 6.8", 6.6),
        ['B'] = ("M.8 .5 L.8 10 M.8 .5 L3 .5 Q5.2 .5 5.2 2.8 Q5.2 5 3 5 L.8 5 M3 5 Q5.8 5 5.8 7.4 Q5.8 10 3 10 L.8 10", 6.2),
        ['C'] = ("M6 2 Q5 .5 3.4 .5 Q.6 .5 .6 5.2 Q.6 10 3.4 10 Q5 10 6 8.5", 6.2),
        ['D'] = ("M.8 .5 L.8 10 L2.8 10 Q6.2 10 6.2 5.2 Q6.2 .5 2.8 .5 L.8 .5", 6.6),
        ['E'] = ("M5.4 .5 L.8 .5 L.8 10 L5.4 10 M.8 5 L4.6 5", 5.6),
        ['F'] = ("M5.4 .5 L.8 .5 L.8 10 M.8 5 L4.6 5", 5.4),
        ['G'] = ("M6 2 Q5 .5 3.4 .5 Q.6 .5 .6 5.2 Q.6 10 3.4 10 Q6 10 6 7 L6 5.6 L3.8 5.6", 6.6),
        ['H'] = ("M.8 .5 L.8 10 M5.8 .5 L5.8 10 M.8 5 L5.8 5", 6.4),
        ['I'] = ("M1 .5 L1 10", 1.8),
        ['J'] = ("M4 .5 L4 7.8 Q4 10 2 10 Q.6 10 .3 8.6", 4.6),
        ['K'] = ("M.8 .5 L.8 10 M5.4 .5 L.8 6 M2.4 4.4 L5.8 10", 5.8),
        ['L'] = ("M.8 .5 L.8 10 L5 10", 5),
        ['M'] = ("M.8 10 L.8 .5 L4 7.5 L7.2 .5 L7.2 10", 7.8),
        ['N'] = ("M.8 10 L.8 .5 L5.8 10 L5.8 .5", 6.4),
        ['O'] = ("M3.4 .5 Q.6 .5 .6 5.2 Q.6 10 3.4 10 Q6.2 10 6.2 5.2 Q6.2 .5 3.4 .5", 6.6),
        ['P'] = ("M.8 10 L.8 .5 L3 .5 Q5.6 .5 5.6 3 Q5.6 5.6 3 5.6 L.8 5.6", 5.8),
        ['R'] = ("M.8 10 L.8 .5 L3 .5 Q5.6 .5 5.6 3 Q5.6 5.6 3 5.6 L.8 5.6 M3 5.6 L5.8 10", 6),
        ['S'] = ("M5.4 2 Q4.6 .5 3 .5 Q.8 .5 .8 2.7 Q.8 4.6 3 5.2 Q5.6 5.8 5.6 7.8 Q5.6 10 3 10 Q1.2 10 .4 8.4", 5.8),
        ['T'] = ("M.2 .5 L6 .5 M3.1 .5 L3.1 10", 6),
        ['U'] = ("M.8 .5 L.8 7.2 Q.8 10 3.3 10 Q5.8 10 5.8 7.2 L5.8 .5", 6.4),
        ['V'] = ("M.3 .5 L3.2 10 L6.1 .5", 6.2),
        ['W'] = ("M.3 .5 L2.3 10 L4.4 3 L6.5 10 L8.5 .5", 8.6),
        ['Z'] = ("M.7 .5 L5.5 .5 L.7 10 L5.7 10", 6.2),
        ['0'] = ("M2.6 .5 Q.5 .5 .5 5.2 Q.5 10 2.6 10 Q4.7 10 4.7 5.2 Q4.7 .5 2.6 .5", 5),
        ['1'] = ("M1 2.4 L2.8 .5 L2.8 10", 3.8),
        ['2'] = ("M.6 2.6 Q1 .5 2.8 .5 Q4.8 .5 4.8 2.8 Q4.8 4.8 .6 10 L5 10", 5.2),
        ['3'] = ("M.6 1.8 Q1.4 .5 2.8 .5 Q4.8 .5 4.8 2.6 Q4.8 4.8 2.4 5 Q5 5.2 5 7.6 Q5 10 2.6 10 Q1 10 .4 8.6", 5.2),
        ['4'] = ("M3.8 10 L3.8 .5 L.4 7.2 L5.2 7.2", 5.4),
        ['5'] = ("M4.6 .5 L1.2 .5 L.9 4.8 Q1.8 4 3 4.2 Q5 4.5 5 7.1 Q5 10 2.6 10 Q1.2 10 .5 8.9", 5.2),
        ['6'] = ("M4.4 1 Q3.6 .5 2.9 .5 Q.6 .5 .6 6 Q.6 10 2.8 10 Q5 10 5 7.6 Q5 5 2.8 5 Q1.2 5 .6 6.6", 5.2),
        ['7'] = ("M.5 .5 L5 .5 L2 10", 5),
        ['8'] = ("M2.7 5 Q.8 4.6 .8 2.7 Q.8 .5 2.7 .5 Q4.6 .5 4.6 2.7 Q4.6 4.6 2.7 5 Q.5 5.4 .5 7.5 Q.5 10 2.7 10 Q4.9 10 4.9 7.5 Q4.9 5.4 2.7 5", 5.2),
        ['9'] = ("M4.8 3.8 Q4.2 5.4 2.6 5.4 Q.6 5.4 .6 3 Q.6 .5 2.7 .5 Q4.8 .5 4.8 4 Q4.8 10 2.2 10 Q1.2 10 .6 9.4", 5.2),
        ['.'] = ("M.8 9.8 L.8 10", 1.6),
        [','] = ("M1 9.6 L.6 11.2", 1.6),
        [':'] = ("M.8 5.4 L.8 5.6 M.8 9.8 L.8 10", 1.6),
        ['-'] = ("M.6 6.8 L3.6 6.8", 4.2),
        ['\''] = ("M.8 .8 L.6 2.8", 1.4),
    };
}
