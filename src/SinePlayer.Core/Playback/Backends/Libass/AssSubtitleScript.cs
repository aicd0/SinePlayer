// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Backends.Libass;

/// <summary>
/// Turns the cues on screen into the script libass parses: the stream's own header, if it has one, followed by one
/// dialogue line per cue.
/// <para>
/// The cues are the same ASS event lines the decoder produces, so nothing is interpreted here — but their timing is not:
/// libavcodec's line carries no start or end, and the cue's own are used instead, which keeps the script and the clock
/// agreeing even where a track's stated times would not.
/// </para>
/// </summary>
internal static class AssSubtitleScript
{
    /// <summary>
    /// What a stream without a header is drawn with: a style table is not optional for libass, and a plain text format has
    /// none of its own. The numbers are the conventional ones for a 384×288 script, scaled by libass to the real frame.
    /// </summary>
    internal const string DefaultHeader =
        "[Script Info]\n" +
        "ScriptType: v4.00+\n" +
        "PlayResX: 384\n" +
        "PlayResY: 288\n" +
        "WrapStyle: 0\n" +
        "ScaledBorderAndShadow: yes\n" +
        "\n" +
        "[V4+ Styles]\n" +
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
        "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,0\n" +
        "\n";

    private const string EventFormat =
        "[Events]\n" +
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    /// <summary>
    /// Composes the script for one set of cues. Cues without text — a bitmap track, which is not this renderer's work —
    /// are left out rather than written as empty lines.
    /// </summary>
    internal static string Compose(string? header, IReadOnlyList<SubtitleCue> cues)
    {
        string scriptHeader = string.IsNullOrWhiteSpace(header) ? DefaultHeader : header;
        StringBuilder builder = new(scriptHeader.Length + (cues.Count * 96) + EventFormat.Length);

        builder.Append(scriptHeader);
        if (!scriptHeader.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append(EventFormat);
        foreach (SubtitleCue cue in cues)
        {
            if (string.IsNullOrEmpty(cue.Text))
            {
                continue;
            }

            // An open-ended cue is one whose end the track never stated, and libass needs a number: the frame it is drawn
            // in is the one that matters, and the window closes it as soon as the event after it arrives.
            TimeSpan end = cue.IsOpenEnded ? cue.Start + TimeSpan.FromSeconds(10) : cue.End;
            if (end <= cue.Start)
            {
                continue;
            }

            builder.Append("Dialogue: ");
            AppendDialogue(builder, cue.Text, cue.Start, end);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes one cue as a dialogue line. The decoder's line is
    /// <c>Layer,Style,Speaker,MarginL,MarginR,MarginV,Effect,Text</c> — the text is everything from the eighth field —
    /// while a dialogue line starts with the timing, so the fields are moved rather than reused.
    /// </summary>
    private static void AppendDialogue(StringBuilder builder, string eventLine, TimeSpan start, TimeSpan end)
    {
        string[] fields = eventLine.Split(',', 8);
        if (fields.Length == 8)
        {
            builder.Append(fields[0].Trim()).Append(',');      // Layer
            AppendTime(builder, start).Append(',');
            AppendTime(builder, end).Append(',');
            builder.Append(fields[1].Trim()).Append(',');      // Style
            builder.Append(fields[2].Trim()).Append(',');      // Name
            builder.Append(fields[3].Trim()).Append(',');      // MarginL
            builder.Append(fields[4].Trim()).Append(',');      // MarginR
            builder.Append(fields[5].Trim()).Append(',');      // MarginV
            builder.Append(fields[6].Trim()).Append(',');      // Effect
            builder.Append(fields[7]);                          // Text, commas and all
            return;
        }

        // A line that is only text: what a decoder that was given no event metadata produces.
        builder.Append("0,");
        AppendTime(builder, start).Append(',');
        AppendTime(builder, end).Append(",Default,,0,0,0,,");
        builder.Append(eventLine);
    }

    /// <summary>A script time, <c>H:MM:SS.cc</c>, which is all the format carries: hundredths of a second.</summary>
    private static StringBuilder AppendTime(StringBuilder builder, TimeSpan value)
    {
        TimeSpan clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        int hours = (int)clamped.TotalHours;
        int centiseconds = clamped.Milliseconds / 10;

        return builder.Append(hours).Append(':')
            .Append(clamped.Minutes.ToString("00")).Append(':')
            .Append(clamped.Seconds.ToString("00")).Append('.')
            .Append(centiseconds.ToString("00"));
    }
}
