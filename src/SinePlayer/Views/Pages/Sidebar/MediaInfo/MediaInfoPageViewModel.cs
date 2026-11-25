// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Threading;
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

    private bool _isTargetSelectorVisible = false;
    public bool IsTargetSelectorVisible
    {
        get => _isTargetSelectorVisible;
        private set
        {
            _isTargetSelectorVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTargetSelectorVisible)));
        }
    }

    private int _selectedTargetIndex = -1;
    public int SelectedTargetIndex
    {
        get => _selectedTargetIndex;
        private set
        {
            _selectedTargetIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTargetIndex)));
        }
    }

    private bool _isMedia = false;
    public bool IsMedia
    {
        get => _isMedia;
        private set
        {
            _isMedia = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMedia)));
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
        }
    }

    private string _releaseDateText = string.Empty;
    public string ReleaseDateText
    {
        get => _releaseDateText;
        set
        {
            _releaseDateText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReleaseDateText)));
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

    private string? _coverImageUri = null;
    public string? CoverImageUri
    {
        get => _coverImageUri;
        private set
        {
            _coverImageUri = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageUri)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCoverImageVisible)));
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

    public MediaModel? Media => _selectedMedia;
    public PlayQueueModel PlayQueue { get; set; } = PlayQueueModel.CreateEmpty();
    public PlaybackModel? Playback { get; set; }
    public bool IsCoverImageVisible => !string.IsNullOrEmpty(CoverImageUri);
    public ObservableCollection<MediaModel> Targets { get; } = [];
    public ObservableCollection<TagCollectionViewModel> MediaTags { get; } = [];
    public ObservableCollection<string> ImageDescriptions { get; } = [];

    public readonly MutableLiveData<string> MediaDescriptionLiveData = new();
    public readonly MutableLiveData<CompletionStatusEnum> CompletionStatusLiveData = new();

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private MediaModel? _rootMedia;
    private MediaModel? _selectedMedia;
    private IReadOnlyList<string> _imageDescriptions = [];
    private List<MediaModel> _containingCollections = [];

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
    }

    public void SetMedia(MediaModel? media)
    {
        if (_rootMedia == media)
        {
            return;
        }

        _rootMedia = media;
        _containingCollections = [];
        RebuildTargets();
        RefreshTargets();
    }

    public void SetImageDescriptions(IReadOnlyList<string> imageDescriptions)
    {
        _imageDescriptions = imageDescriptions;
        UpdateImageDescriptions();
    }

    public void OnMediaUpdated(IEnumerable<long> ids)
    {
        HashSet<long> updatedIds = [.. ids];
        if (!Targets.Any(x => updatedIds.Contains(x.Id)))
        {
            return;
        }

        RebuildTargets();
    }

    public void RefreshTargets()
    {
        MediaModel? root = _rootMedia;
        if (root is null || root.Id < 0)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            IReadOnlySet<long> collectionIds = await CollectionModel.GetCollectionIdsContainingAll((List<long>)[root.Id]);
            List<MediaModel> collections = await MediaModel.BatchFromId(collectionIds);
            collections.Sort(CompareByTitle);
            await MainThreadUtils.RunInMainThread(() =>
            {
                if (_rootMedia != root)
                {
                    return;
                }

                _containingCollections = collections;
                RebuildTargets();
            });
        });
    }

    public void SetSelectedTargetIndex(int index)
    {
        if (index < 0 || index >= Targets.Count || index == _selectedTargetIndex)
        {
            return;
        }

        SelectTarget(Targets[index]);
    }

    public bool AddNewTags(string command)
    {
        MediaModel? media = _selectedMedia;
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

    private void RebuildTargets()
    {
        MediaModel? selected = _selectedMedia;
        Targets.Clear();
        if (_rootMedia is { } root)
        {
            Targets.Add(root);
            foreach (MediaModel collection in _containingCollections)
            {
                Targets.Add(collection);
            }
        }

        SelectedTargetIndex = -1;
        SelectTarget(selected);
        IsTargetSelectorVisible = Targets.Count > 1;
    }

    private void SelectTarget(MediaModel? target)
    {
        int index = target is null ? -1 : Targets.IndexOf(target);
        if (index < 0)
        {
            target = Targets.Count > 0 ? Targets[0] : null;
            index = target is null ? -1 : 0;
        }

        if (_selectedMedia == target && _selectedTargetIndex == index)
        {
            return;
        }

        _selectedMedia = target;
        SelectedTargetIndex = index;
        LoadMediaInfo();
    }

    private void LoadMediaInfo()
    {
        MediaModel? media = _selectedMedia;
        if (media == null)
        {
            IsEmpty = true;
            return;
        }

        IsEmpty = false;

        if (media.Title1.Length == 0)
        {
            MediaTitle1 = media.Title;
            MediaTitle2 = string.Empty;
        }
        else
        {
            MediaTitle1 = media.Title1;
            MediaTitle2 = media.Title2;
        }

        ReleaseDateText = media.ReleaseDate is { } releaseDate ?
            releaseDate.ToString("d", EnvironmentProvider.Instance.GetCurrentAppLanguageInfo()) :
            string.Empty;

        MediaDescriptionLiveData.Emit(media.Description);

        MediaDir = media.Location;
        IsMedia = !media.IsCollection;
        CoverImageUri = MediaExt.GetCoverImageUri(media);
        UpdateImageDescriptions();

        TagCollectionViewModel.Update(MediaTags, media, _actionHandler);
        HasAnyTags = MediaTags.Count > 0;
        CompletionStatusLiveData.Emit(media.CompletionStatus);

        int rating = media.Rating;
        Rating = rating >= 0 ? rating * 0.05F : -1.0;
    }

    private void UpdateImageDescriptions()
    {
        ImageDescriptions.Clear();
        if (_selectedMedia != _rootMedia)
        {
            return;
        }

        foreach (string imageDescription in _imageDescriptions)
        {
            ImageDescriptions.Add(imageDescription);
        }
    }

    private static int CompareByTitle(MediaModel a, MediaModel b)
    {
        int result = string.Compare(a.Title, b.Title, ignoreCase: true);
        return result != 0 ? result : a.Id.CompareTo(b.Id);
    }
}
