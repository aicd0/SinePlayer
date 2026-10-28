// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

namespace SinePlayer.Core.Playback;

/// <summary>
/// One subtitle event as libavcodec hands it over: an ASS dialogue line, because that is what every text format is
/// normalised to. The line is
/// <c>ReadOrder,Layer,Style,Speaker,MarginL,MarginR,MarginV,Effect,Text</c> — the reader's own numbering leads, and the
/// text is the ninth field, which is the whole rest of the line. The timer fields are not in it: those are the cue's
/// own, which is the other half of why a cue can be read out of a packet on its own.
/// <para>
/// This is the one place the shape of that line is known, so a renderer does not have to cut strings itself and a
/// change in the decoder's normalisation cannot silently reach a drawing routine. The override tags in the text are
/// removed rather than interpreted: what is modelled is the words and where their lines break, which is what a
/// first-line renderer can honestly draw. Styling beyond that is the renderer's to grow into, and its tags survive in
/// <see cref="Raw"/> for when it does.
/// </para>
/// </summary>
internal readonly record struct AssSubtitleText
{
    /// <summary>The drawing layer: a later layer covers an earlier one.</summary>
    internal int Layer { get; private init; }

    /// <summary>The name of the style the line asks for, which only the file's own style table could resolve.</summary>
    internal string Style { get; private init; }

    /// <summary>The words, one entry per line, in the order they are stacked.</summary>
    internal IReadOnlyList<string> Lines { get; private init; }

    /// <summary>
    /// Where the text is anchored, as the numeric pad the ASS alignment is: <c>1</c> bottom left through <c>9</c> top
    /// right, with <c>2</c> bottom centre as the default. The written position tag wins over the style, because a line
    /// that says where it goes knows better than a style table this type cannot see.
    /// </summary>
    internal int Alignment { get; private init; }

    /// <summary>The text as it arrived, tags and all, for a renderer that can interpret more than this type does.</summary>
    internal string Raw { get; private init; }

    /// <summary>Whether there is anything to draw at all. A default value is nothing, since it never held a line.</summary>
    internal bool IsEmpty => Lines is not { Count: > 0 };

    /// <summary>
    /// Reads one ASS dialogue line. A line whose shape is not understood is not an error — it is one event of a file
    /// that may be malformed — so the words that can be recovered are returned rather than nothing.
    /// </summary>
    internal static AssSubtitleText Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return default;
        }

        // Nine fields, and the ninth is the whole rest of the line: dialogue is allowed to contain commas.
        string[] fields = line.Split(',', 9);
        int textIndex = fields.Length == 9 ? 8 : -1;
        string text = textIndex >= 0 ? fields[textIndex] : line;
        int layer = textIndex >= 0 && int.TryParse(fields[1].Trim(), out int parsedLayer) ? parsedLayer : 0;
        string style = textIndex >= 0 ? fields[2].Trim() : string.Empty;

        List<string> lines = [];
        int alignment = 0;
        WritePlainText(text, lines, ref alignment);

        return new AssSubtitleText
        {
            Layer = layer,
            Style = style,
            Lines = lines,
            Alignment = alignment == 0 ? 2 : alignment,
            Raw = text,
        };
    }

    /// <summary>
    /// Cuts the override blocks of ASS text out and splits what is left into lines. A block is anything between braces;
    /// inside it the one tag that matters here is <c>\an</c>, which says where the line is anchored.
    /// </summary>
    private static void WritePlainText(string text, List<string> lines, ref int alignment)
    {
        StringBuilder current = new();
        int index = 0;
        while (index < text.Length)
        {
            char character = text[index];
            switch (character)
            {
                case '{':
                    {
                        int end = text.IndexOf('}', index);
                        if (end < 0)
                        {
                            // An unclosed block runs to the end: drawing the tags as words would be worse than dropping them.
                            index = text.Length;
                            break;
                        }

                        ReadAlignment(text.Substring(index + 1, end - index - 1), ref alignment);
                        index = end + 1;
                        break;
                    }

                case '\\' when index + 1 < text.Length:
                    {
                        char next = text[index + 1];
                        index += 2;
                        switch (next)
                        {
                            case 'N':
                            case 'n':
                                lines.Add(current.ToString().Trim());
                                current.Clear();
                                break;
                            case 'h':
                                current.Append(' ');
                                break;
                            default:
                                // Any other escape outside a block is a literal, tag or not; keeping the character is
                                // closer to the file than dropping it.
                                current.Append('\\').Append(next);
                                break;
                        }

                        break;
                    }

                default:
                    current.Append(character);
                    index++;
                    break;
            }
        }

        lines.Add(current.ToString().Trim());

        // A line that ends up saying nothing is a drawing for a bitmap or an empty event, not a line of words.
        lines.RemoveAll(static line => line.Length == 0);
    }

    /// <summary>Finds <c>\an&lt;digit&gt;</c> in an override block, which is the only tag this type acts on.</summary>
    private static void ReadAlignment(string block, ref int alignment)
    {
        int index = block.IndexOf("\\an", StringComparison.Ordinal);
        while (index >= 0)
        {
            int digit = index + 3;
            if (digit < block.Length && block[digit] is >= '1' and <= '9')
            {
                alignment = block[digit] - '0';
                return;
            }

            index = block.IndexOf("\\an", index + 3, StringComparison.Ordinal);
        }
    }
}
