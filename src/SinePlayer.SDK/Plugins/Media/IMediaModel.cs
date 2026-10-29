// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.SDK.Plugins.Media;

public interface IMediaModel
{
    long Id { get; }

    string Location { get; }

    int PageCount { get; }

    string Title1 { get; }

    string Title2 { get; }

    string Description { get; }

    int Rating { get; }

    IReadOnlyDictionary<string, IMediaTagCategory> Tags { get; }

    IReadOnlyDictionary<string, string> Links { get; }

    bool IsHidden { get; }

    CompletionStatusEnum CompletionStatus { get; }

    Task SetTitle1(string title);

    Task SetTitle2(string title);

    Task SetDescription(string description);

    Task SetRating(int rating);

    Task SetTags<T>(IEnumerable<KeyValuePair<string, T>> tags) where T : IEnumerable<string>;

    Task SetLinks(IEnumerable<KeyValuePair<string, string>> links);

    Task SetHidden(bool isHidden);

    Task SetCompletionStatus(CompletionStatusEnum status);
}
