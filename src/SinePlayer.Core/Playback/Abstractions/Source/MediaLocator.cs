// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Source;

/// <summary>
/// Where media comes from. The scheme is what source providers match on, which is how a local path
/// and a network URL travel through the same pipeline.
/// </summary>
public sealed class MediaLocator
{
    public const string SchemeFile = "file";

    private MediaLocator(string value, string scheme)
    {
        Value = value;
        Scheme = scheme;
    }

    /// <summary>Locator exactly as the caller supplied it.</summary>
    public string Value { get; }

    /// <summary>Lower-case scheme, for example <c>file</c>, <c>http</c> or <c>https</c>.</summary>
    public string Scheme { get; }

    public bool IsFile => Scheme == SchemeFile;

    public static MediaLocator Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Scheme))
        {
            return new MediaLocator(value, uri.Scheme.ToLowerInvariant());
        }

        return new MediaLocator(value, SchemeFile);
    }

    public override string ToString()
    {
        return $"{Scheme}:{Value}";
    }
}
