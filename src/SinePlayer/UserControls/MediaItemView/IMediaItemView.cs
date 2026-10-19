// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.ViewModels;

namespace SinePlayer.UserControls.MediaItemView;

interface IMediaItemView
{
    void SetMediaModel(MediaItemViewModel? item);
}
