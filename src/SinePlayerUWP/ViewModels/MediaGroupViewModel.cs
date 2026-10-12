// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace SinePlayerUWP.ViewModels;

internal partial class MediaGroupViewModel(string groupName, IEnumerable<MediaItemViewModel> items, bool collapsed) : SimpleGroupViewModel<MediaItemViewModel>(groupName, items, collapsed)
{
    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set
        {
            _description = value;
            NotifyPropertyChanged(nameof(Description));
        }
    }
}
