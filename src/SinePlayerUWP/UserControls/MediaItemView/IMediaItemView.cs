// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.ViewModels;

namespace SinePlayerUWP.UserControls.MediaItemView;

interface IMediaItemView
{
    void SetMediaModel(MediaItemViewModel? item);
}
