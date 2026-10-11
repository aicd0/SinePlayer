// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Common.Misc;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;

namespace SinePlayerUWP.ViewModels;

internal partial class MediaItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    //
    // properties
    //

    public MediaModel Media { get; }

    public bool IsCollection => Media.IsCollection;
    public bool IsRatingVisible => !string.IsNullOrEmpty(Rating);
    public int TitleMaxLines => IsCollection && IsRatingVisible ? 2 : 3;

    public bool IsCoverImageVisible => !string.IsNullOrEmpty(_coverImageUri);

    public string CoverPlaceholderGlyph
    {
        get
        {
            if (IsCollection)
            {
                return "\uF5ED";
            }

            return AppInfoProvider.IsSupportedAudioExtension(Path.GetExtension(Media.Location)) ? "\uE8D6" : "\uE714";
        }
    }

    private string _title;
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    private string _duration;
    public string Duration
    {
        get => _duration;
        set
        {
            if (_duration == value)
            {
                return;
            }

            _duration = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationAndProgress)));
        }
    }

    private string _rating = string.Empty;
    public string Rating
    {
        get => _rating;
        set
        {
            if (_rating == value)
            {
                return;
            }

            _rating = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rating)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRatingVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleMaxLines)));
        }
    }

    private string _progress = string.Empty;
    public string Progress
    {
        get => _progress;
        set
        {
            if (_progress == value)
            {
                return;
            }

            _progress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationAndProgress)));
        }
    }

    public string DurationAndProgress
    {
        get
        {
            string duration = Duration;
            string progress = Progress;
            if (string.IsNullOrEmpty(duration))
            {
                return progress;
            }
            else if (string.IsNullOrEmpty(progress))
            {
                return duration;
            }
            else
            {
                return $"{duration}  ·  {progress}";
            }
        }
    }

    private bool _isFavorite = false;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        }
    }

    private bool _isHidden = false;
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value)
            {
                return;
            }

            _isHidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
        }
    }

    private string? _coverImageUri = null;
    public string? CoverImageUri
    {
        get => _coverImageUri;
        private set
        {
            if (_coverImageUri == value)
            {
                return;
            }

            _coverImageUri = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageUri)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCoverImageVisible)));
        }
    }

    public Action<MediaItemViewModel>? OnClick { get; set; }
    public Func<MediaItemViewModel, Task<List<BaseMenuFlyoutItemModel>>>? OnRequestContextFlyoutAsync { get; set; }

    //
    // Constructors
    //

    public MediaItemViewModel(MediaModel media)
    {
        Media = media;
        _title = Media.Title;
        int rating = media.Rating;
        _rating = rating >= 0 ? Math.Round(rating * 0.05F, 1, MidpointRounding.AwayFromZero).ToString("0.#") : string.Empty;
        _isFavorite = FavoriteModel.Instance.FromId(media.Id) != null;
        _isHidden = media.IsHidden;
        _duration = FormatDuration(media.DurationMs);
        _coverImageUri = MediaExt.GetCoverImageUri(media);
    }

    //
    // Utilities
    //

    private static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
        {
            return string.Empty;
        }

        var duration = TimeSpan.FromMilliseconds(durationMs);
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes}:{duration.Seconds:00}";
    }

    public void Update(MediaItemViewModel item)
    {
        Title = item.Title;
        Rating = item.Rating;
        IsFavorite = item.IsFavorite;
        IsHidden = item.IsHidden;
        Duration = item.Duration;
        CoverImageUri = item.CoverImageUri;

        Progress = item.Progress;
        OnClick = item.OnClick;
        OnRequestContextFlyoutAsync = item.OnRequestContextFlyoutAsync;
    }

    public void UpdateProgress(bool compat)
    {
        switch (Media.CompletionStatus)
        {
            case CompletionStatusEnum.Reading:
                if (compat)
                {
                    Progress = Math.Clamp(Media.Progress, 0, 100).ToString() + "%";
                }
                else
                {
                    Progress = StringResourceProvider.Instance.FinishPercentage
                        .Replace("$percentage", Media.Progress.ToString());
                }

                break;
            default:
                Progress = CompletionStatusService.EnumToString(Media.CompletionStatus);
                break;
        }
    }

    public async Task<FlyoutBase?> CreateContextFlyout()
    {
        if (OnRequestContextFlyoutAsync is null)
        {
            return null;
        }

        List<BaseMenuFlyoutItemModel> menuFlyoutItems = await OnRequestContextFlyoutAsync(this);
        if (menuFlyoutItems.Count == 0)
        {
            return null;
        }

        var flyout = new MenuFlyout();
        foreach (BaseMenuFlyoutItemModel item in menuFlyoutItems)
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        return flyout;
    }
};
