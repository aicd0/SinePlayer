// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.ViewModels;

namespace SinePlayer.UserControls.MediaItemView;

interface IMediaItemView
{
    void SetMediaModel(MediaItemViewModel? item);
}
