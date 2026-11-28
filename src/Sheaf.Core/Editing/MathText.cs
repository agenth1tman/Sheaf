using System.Text;

namespace Sheaf.Core.Editing;

/// <summary>
/// Mathematical text typed the quick way and turned into what a text box shows: x^2 raises the 2, v_0 lowers the 0
/// (braces take more than one letter: e^{-kt}), \frac{a}{b} stacks a over b, \alpha and the other names become their
/// symbols, and the usual
/// pairs of keys become signs (&lt;= ≤, != ≠, -&gt; →, +- ±, sqrt( √( ). Symbols are drawn in
/// <see cref="SymbolFont"/>, which Windows has and which has all of them.
/// </summary>
public static class MathText
{
    /// <summary>The font symbols and equations are written in: installed with Windows, and with every symbol offered here.</summary>
    public const string SymbolFont = "Segoe UI Symbol";

    /// <summary>Names typed after a backslash, and the symbols they stand for.</summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
        ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ",
        ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ", ["chi"] = "χ",
        ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ",
        ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["approx"] = "≈", ["ne"] = "≠", ["neq"] = "≠",
        ["equiv"] = "≡", ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["ll"] = "≪", ["gg"] = "≫", ["propto"] = "∝",
        ["infty"] = "∞", ["sqrt"] = "√", ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["oint"] = "∮", ["partial"] = "∂",
        ["nabla"] = "∇", ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["supset"] = "⊃", ["subseteq"] = "⊆", ["cup"] = "∪",
        ["cap"] = "∩", ["forall"] = "∀", ["exists"] = "∃", ["therefore"] = "∴", ["because"] = "∵",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["leftrightarrow"] = "↔", ["Rightarrow"] = "⇒",
        ["Leftarrow"] = "⇐", ["Leftrightarrow"] = "⇔", ["uparrow"] = "↑", ["downarrow"] = "↓",
        ["deg"] = "°", ["degree"] = "°", ["hbar"] = "ℏ", ["angstrom"] = "Å", ["angle"] = "∠", ["perp"] = "⊥", ["parallel"] = "∥",
        ["cong"] = "≅", ["sim"] = "∼", ["R"] = "ℝ", ["N"] = "ℕ", ["Z"] = "ℤ", ["Q"] = "ℚ", ["C"] = "ℂ",
    };

    /// <summary>Pairs of keys that stand for a sign, longest first.</summary>
    static readonly (string Typed, string Sign)[] Pairs =
    [
        ("<=>", "⇔"), ("<->", "↔"), ("<=", "≤"), (">=", "≥"), ("!=", "≠"), ("~=", "≈"), ("->", "→"), ("<-", "←"),
        ("=>", "⇒"), ("+-", "±"), ("sqrt(", "√("), ("*", "×"),
    ];

    /// <summary>The text in stretches, each normal, raised or lowered.</summary>
    public static List<(string Text, TextScript Script)> Parse(string source)
    {
        var runs = new List<(string Text, TextScript Script)>();
        Read(source, TextScript.Normal, runs);
        // Stretches next to each other in the same place join, but a fraction's top and bottom stay as they are, so
        // two fractions side by side stay two. An empty top or bottom keeps a space, so the pair still stacks.
        var joined = new List<(string Text, TextScript Script)>();
        foreach (var run in runs)
        {
            bool fraction = run.Script is TextScript.Over or TextScript.Under;
            if (run.Text.Length == 0 && !fraction) continue;
            var kept = fraction && run.Text.Length == 0 ? (" ", run.Script) : run;
            if (!fraction && joined.Count > 0 && joined[^1].Script == run.Script) joined[^1] = (joined[^1].Text + run.Text, run.Script);
            else joined.Add(kept);
        }
        return joined;
    }

    /// <summary>The text as plain characters, for a preview or a screen reader.</summary>
    public static string Plain(string source) => string.Concat(Parse(source).Select(run => run.Text));

    static void Read(string text, TextScript script, List<(string, TextScript)> runs)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            runs.Add((plain.ToString(), script));
            plain.Clear();
        }
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '^' or '_' && i + 1 < text.Length)
            {
                Flush();
                // Inside a fraction or a script, raising and lowering again stays where it is.
                TextScript inner = script != TextScript.Normal ? script : c == '^' ? TextScript.Super : TextScript.Sub;
                string group = Group(text, i + 1, out int next);
                Read(group, inner, runs);
                i = next;
                continue;
            }
            if (c == '\\' && i + 1 < text.Length)
            {
                int end = i + 1;
                while (end < text.Length && char.IsLetter(text[end])) end++;
                if (text[(i + 1)..end] == "frac" && end < text.Length)
                {
                    // A fraction: its top, then its bottom. Spaces in it don't break between lines.
                    Flush();
                    string top = Group(text, SkipSpaces(text, end), out int afterTop);
                    string bottom = afterTop < text.Length ? Group(text, SkipSpaces(text, afterTop), out i) : "";
                    if (afterTop >= text.Length) i = text.Length;
                    var parts = new List<(string, TextScript)>();
                    Read(top.Replace(' ', ' '), TextScript.Over, parts);
                    // Each part is one stretch, so the top and the bottom always meet as one pair.
                    runs.Add((string.Concat(parts.Select(part => part.Item1)), TextScript.Over));
                    parts.Clear();
                    Read(bottom.Replace(' ', ' '), TextScript.Under, parts);
                    runs.Add((string.Concat(parts.Select(part => part.Item1)), TextScript.Under));
                    continue;
                }
                if (end > i + 1 && Names.TryGetValue(text[(i + 1)..end], out string? symbol))
                {
                    plain.Append(symbol);
                    i = end;
                    continue;
                }
                if (end == i + 1)
                {
                    // \{, \^ and the like: the character itself.
                    plain.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
            }
            if (Pairs.FirstOrDefault(pair => string.CompareOrdinal(text, i, pair.Typed, 0, pair.Typed.Length) == 0) is { Typed: not null } found)
            {
                plain.Append(found.Sign);
                i += found.Typed.Length;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Flush();
    }

    static int SkipSpaces(string text, int at)
    {
        while (at < text.Length - 1 && text[at] == ' ') at++;
        return at;
    }

    /// <summary>What a ^ or _ raises or lowers: a {group}, or a \name, or one character.</summary>
    static string Group(string text, int start, out int next)
    {
        if (text[start] == '{')
        {
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                {
                    next = i + 1;
                    return text[(start + 1)..i];
                }
            }
            next = text.Length;
            return text[(start + 1)..];
        }
        if (text[start] == '\\')
        {
            int end = start + 1;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            next = end;
            return text[start..end];
        }
        next = start + 1;
        return text[start].ToString();
    }
}
