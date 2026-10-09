// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.SDK.Models;
using SinePlayerUWP.SDK.Plugins.Comic;

namespace SinePlayerUWP.SDK.Plugins.UI;

public delegate void ReadingComicChangedEventHandler(IComicModel? comic);

public interface IWindowContext
{
    event ReadingComicChangedEventHandler? ReadingComicChanged;

    IComicModel? ReadingComic { get; }

    Task<DialogResult> EnqueueDialog(DialogOptions options);

    Task<DialogResult> EnqueueDialog(ContentDialog dialog);
}
