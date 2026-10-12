// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Constants;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Views.Dialogs.EditMediaInfo;

namespace SinePlayer.Views.Pages.Sidebar.MediaInfo;

internal sealed partial class MediaInfoPage : BasePage
{
    private const string REGEX_URL = @"https?:\/\/[a-zA-Z0-9\-._~%]+(?:\.[a-zA-Z0-9\-._~%]+)+(?:\/[^\s]*)?";

    private readonly MediaInfoPageViewModel ViewModel = new();

    public MediaInfoPage()
    {
        InitializeComponent();

        ViewModel.MediaTitle1 = "";
        ViewModel.MediaTitle2 = "";
        ViewModel.MediaDir = "";
        ViewModel.IsEditable = false;
    }

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);
        ViewModel.Initialize(PageActionHandler);
        ObserveData();
    }

    private void ObserveData()
    {
        GlobalEvent.Instance.MediaUpdated.Observe(this, ids =>
        {
            MediaModel? media = ViewModel.Media;
            if (media is null || !ids.Contains(media.Id))
            {
                return;
            }

            ViewModel.Reload();
        });

        GlobalEvent.Instance.FavoriteUpdated.Observe(this, _ =>
        {
            ViewModel.Reload();
        });

        GlobalEvent.Instance.TagInfoUpdated.Observe(this, _ =>
        {
            ViewModel.Reload();
        });

        GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaInfoChanged).ObserveSticky(this, args =>
        {
            ViewModel.SetMedia(args.Media);
            ViewModel.SetImageDescriptions(args.ImageDescriptions);
            ViewModel.PlayQueue = args.PlayQueue;
        });

        GetWindowEventBus().With<PlaybackModel>(EventId.PlaybackChanged).ObserveSticky(this, playback =>
        {
            ViewModel.Playback = playback;
        });

        ViewModel.CompletionStatusLiveData.ObserveSticky(this, completionStatus =>
        {
            SetCompletionStateButton.Label = CompletionStatusService.EnumToString(completionStatus);
        });

        ViewModel.IsExternalMediaLiveData.ObserveSticky(this, isExternal =>
        {
            RcRating.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
            SetCompletionStateButton.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        });

        ViewModel.MediaDescriptionLiveData.ObserveSticky(this, description =>
        {
            FillRichTextInlines(TbMediaDescription.Inlines, description);
            TbMediaDescription.Visibility = TbMediaDescription.Inlines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void SetCompletionStateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        MediaModel? media = ViewModel.Media;
        if (media is null)
        {
            return;
        }

        List<BaseMenuFlyoutItemModel> menuItems = MenuFlyoutItemsCreator.CreateCompletionStatusMenuItems([media]);

        var flyout = new MenuFlyout();
        foreach (BaseMenuFlyoutItemModel item in menuItems)
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        flyout.ShowAt(fe, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top });
    }

    private void MoreAppBarButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            if (sender is not FrameworkElement fe)
            {
                return;
            }

            MediaModel? media = ViewModel.Media;
            if (media is null)
            {
                return;
            }

            List<BaseMenuFlyoutItemModel> menuItems = await MenuFlyoutItemsCreator.CreateMediaMenuItems(
                PageActionHandler,
                media,
                playQueue: ViewModel.PlayQueue.ToBuilder(),
                playback: ViewModel.Playback?.ToBuilder());
            if (menuItems.Count == 0)
            {
                return;
            }

            var flyout = new MenuFlyout();
            foreach (BaseMenuFlyoutItemModel item in menuItems)
            {
                flyout.Items.Add(item.CreateMenuFlyoutItem());
            }

            flyout.ShowAt(fe, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        });
    }

    private void OnRatingControlValueChanged(RatingControl sender, object args)
    {
        int value = (int)sender.Value;
        CoroutineUtils.Run(async () =>
        {
            MediaModel? media = ViewModel.Media;
            if (media is null)
            {
                return;
            }

            await media.SetRating(value < 1 ? -1 : value * 20);
        });
    }

    private void OnDirectoryTapped(object sender, TappedRoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            ErrorResult err = await ErrorLogger.Run(nameof(OnDirectoryTapped), async err =>
            {
                MediaModel? media = ViewModel.Media;
                if (media is null)
                {
                    return err.Error("Media is null.");
                }

                ErrorResult innerErr = await media.ShowInFileExplorer();
                if (!innerErr.IsSuccessful)
                {
                    return err.Error(innerErr);
                }

                return err.Success();
            });

            err.DisplayErrorMessage(PageActionHandler);
        });
    }

    private void OnEditInfoClick(object sender, RoutedEventArgs e)
    {
        MediaModel? media = ViewModel.Media;
        if (media == null)
        {
            return;
        }

        var dialog = new EditMediaInfoDialog([media]);
        CoroutineUtils.Run(() => dialog.ShowAsync(WindowId));
    }

    //
    // New tags
    //

    private readonly Lazy<SearchHistoryModel> _tagHistoryModel = new(() =>
    {
        return SearchHistoryModel.Get("NewTags");
    });

    private void NewTagsAutoSuggestBox_GotFocus(object sender, RoutedEventArgs e)
    {
        var autoSuggestBox = (AutoSuggestBox)sender;
        autoSuggestBox.ItemsSource = SearchTagHistory(autoSuggestBox.Text);
    }

    private void NewTagsAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string text = sender.Text.Trim();
        sender.Text = string.Empty;
        if (ViewModel.AddNewTags(text))
        {
            _tagHistoryModel.Value.Save(text);
        }
    }

    private void NewTagsAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        string text = args.SelectedItem.ToString() ?? string.Empty;
        sender.Text = string.Empty;
        if (ViewModel.AddNewTags(text))
        {
            _tagHistoryModel.Value.Save(text);
        }
    }

    private void NewTagsAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = SearchTagHistory(sender.Text);
        }
    }

    private void NewTagTipButton_Click(object sender, RoutedEventArgs e)
    {
        ThirdPartyLauncher.StartTemporaryTextFile("NewTagsHelp.txt", StringResourceProvider.Instance.EnterNewTagsHint);
    }

    private List<string> SearchTagHistory(string query)
    {
        char[] seperators = [.. LocalizationUtils.Colons, .. LocalizationUtils.Commas];
        string[] keywords = query.Split(seperators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return [.. _tagHistoryModel.Value.Search(keywords, 10)];
    }

    //
    // Utilities
    //

    private static void FillRichTextInlines(InlineCollection inlines, string richText)
    {
        inlines.Clear();

        Regex urlRegex = URL_REGEX();
        MatchCollection matches = urlRegex.Matches(richText);
        int currentIndex = 0;

        foreach (Match match in matches)
        {
            Uri uri;
            try
            {
                uri = new Uri(match.Value);
            }
            catch (Exception)
            {
                continue;
            }

            int startIndex = match.Index;
            int endIndex = match.Index + match.Length;
            if (endIndex <= currentIndex)
            {
                continue;
            }

            if (startIndex > currentIndex)
            {
                var run = new Run
                {
                    Text = richText[currentIndex..startIndex]
                };
                inlines.Add(run);
            }

            {
                var run = new Run
                {
                    Text = match.Value
                };

                var hyperlink = new Hyperlink
                {
                    NavigateUri = uri
                };

                hyperlink.Inlines.Add(run);
                inlines.Add(hyperlink);
            }

            currentIndex = endIndex;
        }

        if (currentIndex < richText.Length)
        {
            var run = new Run
            {
                Text = richText[currentIndex..]
            };

            inlines.Add(run);
        }
    }

    [GeneratedRegex(REGEX_URL, RegexOptions.None)]
    private static partial Regex URL_REGEX();
}
