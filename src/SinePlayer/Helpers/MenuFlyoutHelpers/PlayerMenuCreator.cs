// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Localization;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.Misc;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal static class PlayerMenuCreator
{
    public static async Task<List<BaseMenuFlyoutItemModel>> CreateVideoHostMenuItems(PlayerMenuContext context)
    {
        List<BaseMenuFlyoutItemModel> mediaItems = await MediaMenuCreator.CreateMediaMenuItems(
            context.ActionHandler,
            context.Media,
            playQueue: context.PlayQueue,
            playback: context.Playback);

        return
        [
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Video,
                Icon = new FontIconSource() { Glyph = MediaGlyphHelper.VideoGlyph },
                Items = CreateVideoMenuItems(context),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Audio,
                Icon = new FontIconSource() { Glyph = MediaGlyphHelper.AudioGlyph },
                Items = CreateAudioMenuItems(context),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = TrimText(context.Media.Title),
                Icon = new FontIconSource() { Glyph = MediaGlyphHelper.GetMediaGlyph(context.Media) },
                Items = mediaItems,
            },
        ];
    }

    public static List<BaseMenuFlyoutItemModel> CreateChoiceItems(IReadOnlyList<PlaybackComponentOption> options, string? selectedKey, Action<string> select)
    {
        List<BaseMenuFlyoutItemModel> items = [];
        foreach (PlaybackComponentOption option in options)
        {
            string key = option.Key;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = option.Label,
                IsChecked = selectedKey == key,
                Click = () => select(key),
            });
        }

        return items;
    }

    public static PlaybackComponentOption EnsureChoice(List<PlaybackComponentOption> options, string key)
    {
        foreach (PlaybackComponentOption option in options)
        {
            if (option.Key == key)
            {
                return option;
            }
        }

        PlaybackComponentOption unavailable = new(key, $"{key} ({StringResourceProvider.Instance.Unavailable})");
        options.Add(unavailable);
        return unavailable;
    }

    public static List<PlaybackComponentOption> GetStretchModeOptions()
    {
        return
        [
            new(nameof(VideoResizeMode.Fit), StringResourceProvider.Instance.Fit),
            new(nameof(VideoResizeMode.Stretch), StringResourceProvider.Instance.Stretch),
            new(nameof(VideoResizeMode.Fill), StringResourceProvider.Instance.Fill),
            new(nameof(VideoResizeMode.OriginalSize), StringResourceProvider.Instance.OriginalSize),
        ];
    }

    public static List<PlaybackComponentOption> GetRotationOptions()
    {
        return
        [
            new("0", StringResourceProvider.Instance.None),
            new("90", "90°"),
            new("180", "180°"),
            new("270", "270°"),
        ];
    }

    private static List<BaseMenuFlyoutItemModel> CreateVideoMenuItems(PlayerMenuContext context)
    {
        PlayerSettingsModel settings = context.Settings;
        Action<Action<PlayerSettingsModel>> applyChange = context.UpdateSettings;

        List<PlaybackComponentOption> decoderOptions = [.. PlaybackComponents.GetVideoDecoderOptions()];
        PlaybackComponentOption decoder = EnsureChoice(decoderOptions, settings.VideoDecoder);

        List<PlaybackComponentOption> rendererOptions = [.. PlaybackComponents.GetVideoRendererOptions()];
        PlaybackComponentOption renderer = EnsureChoice(rendererOptions, settings.VideoRenderer);

        List<PlaybackComponentOption> stretchModeOptions = GetStretchModeOptions();
        PlaybackComponentOption stretchMode = EnsureChoice(stretchModeOptions, settings.VideoResizeMode.ToString());

        List<PlaybackComponentOption> rotationOptions = GetRotationOptions();
        PlaybackComponentOption rotation = EnsureChoice(rotationOptions, settings.VideoRotationDegrees.ToString(CultureInfo.InvariantCulture));

        return
        [
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SelectVideoTrack,
                Items = CreateTrackItems(context, MediaStreamKind.Video),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Decoder,
                Items = CreateChoiceItems(decoderOptions, decoder.Key, key =>
                {
                    if (settings.VideoDecoder != key)
                    {
                        applyChange(target => target.VideoDecoder = key);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Renderer,
                Items = CreateChoiceItems(rendererOptions, renderer.Key, key =>
                {
                    if (settings.VideoRenderer != key)
                    {
                        applyChange(target => target.VideoRenderer = key);
                    }
                }),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.PreferHardwareDecoding,
                IsChecked = settings.VideoPreferHardwareDecoding,
                Click = () => applyChange(target => target.VideoPreferHardwareDecoding = !settings.VideoPreferHardwareDecoding),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.PreferHdr,
                IsChecked = settings.VideoPreferHdr,
                Click = () => applyChange(target => target.VideoPreferHdr = !settings.VideoPreferHdr),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Scaling,
                Items = CreateChoiceItems(stretchModeOptions, stretchMode.Key, key =>
                {
                    VideoResizeMode mode = Enum.Parse<VideoResizeMode>(key);
                    if (settings.VideoResizeMode != mode)
                    {
                        applyChange(target => target.VideoResizeMode = mode);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Rotation,
                Items = CreateChoiceItems(rotationOptions, rotation.Key, key =>
                {
                    int degrees = int.Parse(key, CultureInfo.InvariantCulture);
                    if (settings.VideoRotationDegrees != degrees)
                    {
                        applyChange(target => target.VideoRotationDegrees = degrees);
                    }
                }),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FlipVertically,
                IsChecked = settings.VideoFlipVertical,
                Click = () => applyChange(target => target.VideoFlipVertical = !settings.VideoFlipVertical),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FlipHorizontally,
                IsChecked = settings.VideoFlipHorizontal,
                Click = () => applyChange(target => target.VideoFlipHorizontal = !settings.VideoFlipHorizontal),
            },
        ];
    }

    private static List<BaseMenuFlyoutItemModel> CreateAudioMenuItems(PlayerMenuContext context)
    {
        PlayerSettingsModel settings = context.Settings;
        Action<Action<PlayerSettingsModel>> applyChange = context.UpdateSettings;

        List<PlaybackComponentOption> decoderOptions = [.. PlaybackComponents.GetAudioDecoderOptions()];
        PlaybackComponentOption decoder = EnsureChoice(decoderOptions, settings.AudioDecoder);

        List<PlaybackComponentOption> rendererOptions = [.. PlaybackComponents.GetAudioRendererOptions()];
        PlaybackComponentOption renderer = EnsureChoice(rendererOptions, settings.AudioRenderer);

        return
        [
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SelectAudioTrack,
                Items = CreateTrackItems(context, MediaStreamKind.Audio),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Decoder,
                Items = CreateChoiceItems(decoderOptions, decoder.Key, key =>
                {
                    if (settings.AudioDecoder != key)
                    {
                        applyChange(target => target.AudioDecoder = key);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Renderer,
                Items = CreateChoiceItems(rendererOptions, renderer.Key, key =>
                {
                    if (settings.AudioRenderer != key)
                    {
                        applyChange(target => target.AudioRenderer = key);
                    }
                }),
            },
        ];
    }

    private static List<BaseMenuFlyoutItemModel> CreateTrackItems(PlayerMenuContext context, MediaStreamKind kind)
    {
        IReadOnlyList<MediaStreamInfo>? streams = context.Tracks(kind);
        if (streams is null || streams.Count == 0)
        {
            return [new SimpleMenuFlyoutItemModel() { Text = StringResourceProvider.Instance.None, IsEnabled = false }];
        }

        MediaStreamInfo? selected = context.SelectedTrack(kind);

        List<BaseMenuFlyoutItemModel> items = [];
        foreach (MediaStreamInfo stream in streams)
        {
            MediaStreamInfo choice = stream;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = MediaStreamLabel.Describe(choice),
                IsChecked = selected is not null && selected.Index == choice.Index,
                Click = () => context.SelectTrack(kind, choice),
            });
        }

        items.Add(new SeparatorMenuFlyoutItemModel());
        items.Add(new ToggleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Off,
            IsChecked = selected is null,
            Click = () => context.SelectTrack(kind, null),
        });

        return items;
    }

    private static string TrimText(string text)
    {
        const int maxLength = 30;

        if (text.Length <= maxLength)
        {
            return text;
        }

        int count = maxLength / 2 - 1;
        return $"{text[..count]} ... {text[^count..]}";
    }
}
