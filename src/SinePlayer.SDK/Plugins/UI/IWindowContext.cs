// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;

using SinePlayer.SDK.Models;
using SinePlayer.SDK.Plugins.Media;

namespace SinePlayer.SDK.Plugins.UI;

public delegate void ForegroundMediaChangedEventHandler(IMediaModel? media);

public interface IWindowContext
{
    event ForegroundMediaChangedEventHandler? ForegroundMediaChanged;

    IMediaModel? ForegroundMedia { get; }

    Task<DialogResult> EnqueueDialog(DialogOptions options);

    Task<DialogResult> EnqueueDialog(ContentDialog dialog);
}
