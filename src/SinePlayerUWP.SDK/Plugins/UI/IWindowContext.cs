// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.SDK.Models;
using SinePlayerUWP.SDK.Plugins.Media;

namespace SinePlayerUWP.SDK.Plugins.UI;

public delegate void ForegroundMediaChangedEventHandler(IMediaModel? comic);

public interface IWindowContext
{
    event ForegroundMediaChangedEventHandler? ForegroundMediaChanged;

    IMediaModel? ForegroundMedia { get; }

    Task<DialogResult> EnqueueDialog(DialogOptions options);

    Task<DialogResult> EnqueueDialog(ContentDialog dialog);
}
