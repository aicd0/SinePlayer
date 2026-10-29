// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Pages.Sidebar.MediaInfo;

internal partial class MediaInfoPageViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        set
        {
            _isEmpty = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
        }
    }

    private string _mediaTitle1 = string.Empty;
    public string MediaTitle1
    {
        get => _mediaTitle1;
        set
        {
            _mediaTitle1 = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaTitle1)));
        }
    }

    private string _mediaTitle2 = string.Empty;
    public string MediaTitle2
    {
        get => _mediaTitle2;
        set
        {
            _mediaTitle2 = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaTitle2)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMediaTitle2Visible)));
        }
    }

    private double _rating;
    public double Rating
    {
        get => _rating;
        set
        {
            _rating = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rating)));
        }
    }

    private string _mediaDir = "";
    public string MediaDir
    {
        get => _mediaDir;
        set
        {
            _mediaDir = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaDir)));
        }
    }

    private bool _hasAnyTags = false;
    public bool HasAnyTags
    {
        get => _hasAnyTags;
        set
        {
            _hasAnyTags = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnyTags)));
        }
    }

    public MediaModel? Media => _media;
    public PlayQueueModel PlayQueue { get; set; } = PlayQueueModel.CreateEmpty();
    public PlaybackModel? Playback { get; set; }
    public bool IsMediaTitle2Visible => MediaTitle2.Length > 0;
    public ObservableCollection<TagCollectionViewModel> MediaTags { get; } = [];
    public ObservableCollection<string> ImageDescriptions { get; } = [];

    public readonly MutableLiveData<string> MediaDescriptionLiveData = new();
    public readonly MutableLiveData<CompletionStatusEnum> CompletionStatusLiveData = new();

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private MediaModel? _media;

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
    }

    public void SetMedia(MediaModel? media)
    {
        if (_media == media)
        {
            return;
        }

        _media = media;
        ImageDescriptions.Clear();
        LoadMediaInfo();
    }

    public void SetImageDescriptions(IReadOnlyList<string> imageDescriptions)
    {
        ImageDescriptions.Clear();
        foreach (string imageDescription in imageDescriptions)
        {
            ImageDescriptions.Add(imageDescription);
        }
    }

    public bool AddNewTags(string command)
    {
        MediaModel? media = _media;
        if (media is null || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        command = command.ReplaceLineEndings(string.Empty);
        string key = string.Empty;
        string value = command;
        bool overwriteMode = false;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (LocalizationUtils.Commas.Contains(c))
            {
                break;
            }
            else if (LocalizationUtils.Colons.Contains(c))
            {
                key = command[..i];
                overwriteMode = i + 1 < command.Length && LocalizationUtils.Colons.Contains(command[i + 1]);
                value = command[(overwriteMode ? i + 2 : i + 1)..];
                break;
            }
        }

        key = key.Trim();
        if (string.IsNullOrEmpty(key))
        {
            key = StringResourceProvider.Instance.Default;
        }

        string[] values = value.Split(LocalizationUtils.Commas, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Dictionary<string, HashSet<string>> tags = media.TagsCopy;
        if (!tags.TryGetValue(key, out HashSet<string>? categoryTags))
        {
            categoryTags = [];
            tags.Add(key, categoryTags);
        }

        if (overwriteMode)
        {
            categoryTags.Clear();
        }

        foreach (string tag in values)
        {
            categoryTags.Add(tag);
        }

        CoroutineUtils.Run(() => media.SetTags(tags));
        return true;
    }

    public void Reload()
    {
        LoadMediaInfo();
    }

    private void LoadMediaInfo()
    {
        MediaModel? media = _media;
        if (media == null)
        {
            IsEmpty = true;
            return;
        }

        IsEmpty = false;

        if (media.Title1.Length == 0)
        {
            MediaTitle1 = media.Title;
        }
        else
        {
            MediaTitle1 = media.Title1;
            MediaTitle2 = media.Title2;
        }

        MediaDescriptionLiveData.Emit(media.Description);

        MediaDir = media.Location;

        TagCollectionViewModel.Update(MediaTags, media, _actionHandler);
        HasAnyTags = MediaTags.Count > 0;
        CompletionStatusLiveData.Emit(media.CompletionStatus);

        int rating = media.Rating;
        Rating = rating >= 0 ? rating * 0.05F : -1.0;
    }
}
