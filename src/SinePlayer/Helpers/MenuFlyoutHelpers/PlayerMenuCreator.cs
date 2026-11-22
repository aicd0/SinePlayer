// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.HotKey;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Win2D;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.Misc;

using Windows.UI.Text;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal static class PlayerMenuCreator
{
    private static readonly (string Key, double Value)[] AspectRatioOptions =
    [
        ("2.39:1", 2.39),
        ("2.35:1", 2.35),
        ("2:1", 2.0),
        ("1.85:1", 1.85),
        ("16:9", 16.0 / 9.0),
        ("16:10", 1.6),
        ("3:2", 1.5),
        ("4:3", 4.0 / 3.0),
        ("1:1", 1.0),
        ("4:5", 0.8),
        ("9:16", 9.0 / 16.0),
    ];

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
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Subtitle,
                Icon = new FontIconSource() { Glyph = MediaGlyphHelper.SubtitleGlyph },
                Items = CreateSubtitleMenuItems(context),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.NewWindow,
                Icon = new FontIconSource() { Glyph = "\uE8A7" },
                Click = () => OpenInNewWindow(context),
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

    public static List<BaseMenuFlyoutItemModel> CreateStretchModeItems(VideoSettingsModel settings, Action<Action<VideoSettingsModel>> applyChange)
    {
        void Select(VideoResizeMode resizeMode, double aspectRatio)
        {
            if (settings.ResizeMode == resizeMode && Math.Abs(settings.AspectRatio - aspectRatio) < 0.001)
            {
                return;
            }

            applyChange(target =>
            {
                target.ResizeMode = resizeMode;
                target.AspectRatio = aspectRatio;
            });
        }

        return
        [
            CreateStretchModeSubItem(StringResourceProvider.Instance.Fit, VideoResizeMode.Fit, settings, Select),
            CreateStretchModeSubItem(StringResourceProvider.Instance.Fill, VideoResizeMode.Fill, settings, Select),
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Stretch,
                IsChecked = settings.ResizeMode == VideoResizeMode.Stretch,
                Click = () => Select(VideoResizeMode.Stretch, 0.0),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.OriginalSize,
                IsChecked = settings.ResizeMode == VideoResizeMode.OriginalSize,
                Click = () => Select(VideoResizeMode.OriginalSize, 0.0),
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

    public static string GetStretchModeLabel(VideoResizeMode resizeMode, double aspectRatio)
    {
        string label = resizeMode switch
        {
            VideoResizeMode.Fill => StringResourceProvider.Instance.Fill,
            VideoResizeMode.Stretch => StringResourceProvider.Instance.Stretch,
            VideoResizeMode.OriginalSize => StringResourceProvider.Instance.OriginalSize,
            _ => StringResourceProvider.Instance.Fit,
        };

        string? aspectRatioKey = GetAspectRatioKey(aspectRatio);
        return aspectRatioKey is null ? label : $"{label} ({aspectRatioKey})";
    }

    public static List<PlaybackComponentOption> GetScalingModeOptions()
    {
        return
        [
            new(nameof(VideoScalingMode.Point), StringResourceProvider.Instance.ImageInterpolationNearestNeighbor),
            new(nameof(VideoScalingMode.Bilinear), StringResourceProvider.Instance.ImageInterpolationBilinear),
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
        Action<Action<VideoSettingsModel>> applyChange = context.UpdateVideoSettings;

        List<PlaybackComponentOption> decoderOptions = [.. PlaybackComponents.GetVideoDecoderOptions()];
        PlaybackComponentOption decoder = EnsureChoice(decoderOptions, settings.Video.Decoder);

        List<PlaybackComponentOption> rendererOptions = [.. PlaybackComponents.GetVideoRendererOptions()];
        PlaybackComponentOption renderer = EnsureChoice(rendererOptions, settings.Video.Renderer);

        List<PlaybackComponentOption> rotationOptions = GetRotationOptions();
        PlaybackComponentOption rotation = EnsureChoice(rotationOptions, settings.Video.RotationDegrees.ToString(CultureInfo.InvariantCulture));

        List<PlaybackComponentOption> interpolationOptions = GetScalingModeOptions();
        PlaybackComponentOption interpolation = EnsureChoice(interpolationOptions, settings.Video.ScalingMode.ToString());

        return
        [
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SelectVideoTrack,
                Items = CreateVideoTrackItems(context),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Sync,
                Items = CreateSyncMenuItems(context, MediaStreamKind.Video),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Decoder,
                Items = CreateChoiceItems(decoderOptions, decoder.Key, key =>
                {
                    if (settings.Video.Decoder != key)
                    {
                        applyChange(target => target.Decoder = key);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Renderer,
                Items = CreateChoiceItems(rendererOptions, renderer.Key, key =>
                {
                    if (settings.Video.Renderer != key)
                    {
                        applyChange(target => target.Renderer = key);
                    }
                }),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.PreferHardwareDecoding,
                IsChecked = settings.Video.PreferHardwareDecoding,
                Click = () => applyChange(target => target.PreferHardwareDecoding = !settings.Video.PreferHardwareDecoding),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.PreferHdr,
                IsChecked = settings.Video.PreferHdr,
                Click = () => applyChange(target => target.PreferHdr = !settings.Video.PreferHdr),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Scaling,
                Items = CreateStretchModeItems(settings.Video, applyChange),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Rotation,
                Items = CreateChoiceItems(rotationOptions, rotation.Key, key =>
                {
                    int degrees = int.Parse(key, CultureInfo.InvariantCulture);
                    if (settings.Video.RotationDegrees != degrees)
                    {
                        applyChange(target => target.RotationDegrees = degrees);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Zoom,
                Items = new List<BaseMenuFlyoutItemModel>
                {
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.ZoomIn,
                        ShortcutAction = KeyboardShortcutActions.ZoomIn,
                        Click = () => context.AdjustZoom(true),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.ZoomOut,
                        ShortcutAction = KeyboardShortcutActions.ZoomOut,
                        Click = () => context.AdjustZoom(false),
                    },
                },
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.PanPosition,
                Items = new List<BaseMenuFlyoutItemModel>
                {
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanUp,
                        ShortcutAction = KeyboardShortcutActions.PanVideoUp,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanVideoUp),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanDown,
                        ShortcutAction = KeyboardShortcutActions.PanVideoDown,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanVideoDown),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanLeft,
                        ShortcutAction = KeyboardShortcutActions.PanVideoLeft,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanVideoLeft),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanRight,
                        ShortcutAction = KeyboardShortcutActions.PanVideoRight,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanVideoRight),
                    },
                },
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FlipVertically,
                IsChecked = settings.Video.FlipVertical,
                Click = () => applyChange(target => target.FlipVertical = !settings.Video.FlipVertical),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FlipHorizontally,
                IsChecked = settings.Video.FlipHorizontal,
                Click = () => applyChange(target => target.FlipHorizontal = !settings.Video.FlipHorizontal),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.ImageInterpolation,
                Items = CreateChoiceItems(interpolationOptions, interpolation.Key, key =>
                {
                    VideoScalingMode mode = Enum.Parse<VideoScalingMode>(key);
                    if (settings.Video.ScalingMode != mode)
                    {
                        applyChange(target => target.ScalingMode = mode);
                    }
                }),
            },
        ];
    }

    private static List<BaseMenuFlyoutItemModel> CreateVideoTrackItems(PlayerMenuContext context)
    {
        IReadOnlyList<MediaStreamInfo>? streams = context.Tracks(MediaStreamKind.Video);
        if (streams is null || streams.Count == 0)
        {
            return [new SimpleMenuFlyoutItemModel() { Text = StringResourceProvider.Instance.None, IsEnabled = false }];
        }

        List<BaseMenuFlyoutItemModel> items = [];
        foreach (MediaStreamInfo stream in streams)
        {
            MediaStreamInfo choice = stream;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = MediaStreamLabel.Describe(choice, context.SourceOf(choice)),
                IsChecked = context.IsTrackPlaying(choice),
                Click = () => context.SelectTrack(MediaStreamKind.Video, choice),
            });
        }

        items.Add(new SeparatorMenuFlyoutItemModel());
        items.Add(new ToggleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Off,
            IsChecked = !streams.Any(context.IsTrackPlaying),
            Click = () => context.SelectTrack(MediaStreamKind.Video, null),
        });

        return items;
    }

    private static List<BaseMenuFlyoutItemModel> CreateAudioMenuItems(PlayerMenuContext context)
    {
        PlayerSettingsModel settings = context.Settings;
        Action<Action<AudioSettingsModel>> applyChange = context.UpdateAudioSettings;

        List<PlaybackComponentOption> decoderOptions = [.. PlaybackComponents.GetAudioDecoderOptions()];
        PlaybackComponentOption decoder = EnsureChoice(decoderOptions, settings.Audio.Decoder);

        List<PlaybackComponentOption> rendererOptions = [.. PlaybackComponents.GetAudioRendererOptions()];
        PlaybackComponentOption renderer = EnsureChoice(rendererOptions, settings.Audio.Renderer);

        return
        [
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SelectAudioTrack,
                Items = CreateAudioTrackItems(context),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Sync,
                Items = CreateSyncMenuItems(context, MediaStreamKind.Audio),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Decoder,
                Items = CreateChoiceItems(decoderOptions, decoder.Key, key =>
                {
                    if (settings.Audio.Decoder != key)
                    {
                        applyChange(target => target.Decoder = key);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Renderer,
                Items = CreateChoiceItems(rendererOptions, renderer.Key, key =>
                {
                    if (settings.Audio.Renderer != key)
                    {
                        applyChange(target => target.Renderer = key);
                    }
                }),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.StretchMethod,
                Items = CreateChoiceItems(GetAudioStretchMethodOptions(), settings.Audio.StretchMethod.ToString(), key =>
                {
                    AudioSpeedAlgorithm method = Enum.Parse<AudioSpeedAlgorithm>(key);
                    if (settings.Audio.StretchMethod != method)
                    {
                        applyChange(target => target.StretchMethod = method);
                    }
                }),
            },
        ];
    }

    private static List<BaseMenuFlyoutItemModel> CreateAudioTrackItems(PlayerMenuContext context)
    {
        bool allowMulti = context.AllowMultiAudioTracks;

        List<BaseMenuFlyoutItemModel> items = [];

        IReadOnlyList<MediaStreamInfo>? streams = context.Tracks(MediaStreamKind.Audio);
        if (streams is null || streams.Count == 0)
        {
            items.Add(new SimpleMenuFlyoutItemModel() { Text = StringResourceProvider.Instance.None, IsEnabled = false });
            return items;
        }

        bool playing = streams.Any(context.IsTrackPlaying);

        foreach (MediaStreamInfo stream in streams)
        {
            MediaStreamInfo choice = stream;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = MediaStreamLabel.Describe(choice, context.SourceOf(choice)),
                IsChecked = context.IsTrackPlaying(choice),
                Click = () =>
                {
                    if (allowMulti)
                    {
                        context.ToggleAudioTrack(choice);
                    }
                    else
                    {
                        context.SelectTrack(MediaStreamKind.Audio, choice);
                    }
                },
            });
        }

        items.Add(new SeparatorMenuFlyoutItemModel());
        items.Add(new ToggleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.AllowMultiTracks,
            IsChecked = allowMulti,
            Click = () => context.SetAllowMultiAudioTracks(!allowMulti),
        });
        items.Add(new ToggleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Off,
            IsChecked = !playing,
            Click = () =>
            {
                if (allowMulti)
                {
                    context.ClearAudioTracks();
                }
                else
                {
                    context.SelectTrack(MediaStreamKind.Audio, null);
                }
            },
        });

        return items;
    }

    private static List<PlaybackComponentOption> GetAudioStretchMethodOptions()
    {
        return
        [
            new(nameof(AudioSpeedAlgorithm.None), StringResourceProvider.Instance.None),
            new(nameof(AudioSpeedAlgorithm.WSOLA), nameof(AudioSpeedAlgorithm.WSOLA)),
        ];
    }

    private static List<BaseMenuFlyoutItemModel> CreateSubtitleMenuItems(PlayerMenuContext context)
    {
        PlayerSettingsModel settings = context.Settings;
        Action<Action<SubtitleSettingsModel>> applyChange = context.UpdateSubtitleSettings;

        List<PlaybackComponentOption> decoderOptions = [.. PlaybackComponents.GetSubtitleDecoderOptions()];
        PlaybackComponentOption decoder = EnsureChoice(decoderOptions, settings.Subtitle.Decoder);

        List<PlaybackComponentOption> rendererOptions = [.. PlaybackComponents.GetSubtitleRendererOptions()];
        PlaybackComponentOption renderer = EnsureChoice(rendererOptions, settings.Subtitle.Renderer);

        return
        [
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SelectSubtitleTrack,
                Items = CreateSubtitleTrackItems(context),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Sync,
                Items = CreateSyncMenuItems(context, MediaStreamKind.Subtitle),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Decoder,
                Items = CreateChoiceItems(decoderOptions, decoder.Key, key =>
                {
                    if (settings.Subtitle.Decoder != key)
                    {
                        applyChange(target => target.Decoder = key);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Renderer,
                Items = CreateChoiceItems(rendererOptions, renderer.Key, key =>
                {
                    if (settings.Subtitle.Renderer != key)
                    {
                        applyChange(target => target.Renderer = key);
                    }
                }),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FontFamily,
                Items = CreateChoiceItems(GetSubtitleFontFamilyOptions(), settings.Subtitle.Family, key =>
                {
                    if (!string.Equals(settings.Subtitle.Family, key, StringComparison.Ordinal))
                    {
                        applyChange(target => target.Family = key);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FontSize,
                Items = new List<BaseMenuFlyoutItemModel>
                {
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.IncreaseFontSize,
                        ShortcutAction = KeyboardShortcutActions.IncreaseSubtitleFontSize,
                        Click = () => context.AdjustSubtitleFontSize(true),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.DecreaseFontSize,
                        ShortcutAction = KeyboardShortcutActions.DecreaseSubtitleFontSize,
                        Click = () => context.AdjustSubtitleFontSize(false),
                    },
                },
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FontStyle,
                Items = CreateChoiceItems(GetSubtitleFontStyleOptions(), settings.Subtitle.ToFontStyle().ToString(), key =>
                {
                    FontStyle style = Enum.Parse<FontStyle>(key);
                    if (settings.Subtitle.ToFontStyle() != style)
                    {
                        applyChange(target => target.Style = style.ToString());
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.FontWeight,
                Items = CreateChoiceItems(GetSubtitleFontWeightOptions(settings.Subtitle.Family), GetSubtitleFontWeightKey(settings.Subtitle.Weight), key =>
                {
                    int weight = int.Parse(key, CultureInfo.InvariantCulture);
                    if (settings.Subtitle.Weight != weight)
                    {
                        applyChange(target => target.Weight = weight);
                    }
                }),
            },
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Alignment,
                Items = CreateChoiceItems(GetSubtitleAlignmentOptions(), settings.Subtitle.TextAlignment?.ToString() ?? string.Empty, key =>
                {
                    SubtitleTextAlignment? alignment = key.Length == 0 ? null : Enum.Parse<SubtitleTextAlignment>(key);
                    if (settings.Subtitle.TextAlignment != alignment)
                    {
                        applyChange(target => target.TextAlignment = alignment);
                    }
                }),
            },
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Shadow,
                IsChecked = settings.Subtitle.ShadowEnabled,
                Click = () => applyChange(target => target.ShadowEnabled = !settings.Subtitle.ShadowEnabled),
            },
            new SeparatorMenuFlyoutItemModel(),
            new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.PanPosition,
                Items = new List<BaseMenuFlyoutItemModel>
                {
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanUp,
                        ShortcutAction = KeyboardShortcutActions.PanSubtitleUp,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanSubtitleUp),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanDown,
                        ShortcutAction = KeyboardShortcutActions.PanSubtitleDown,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanSubtitleDown),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanLeft,
                        ShortcutAction = KeyboardShortcutActions.PanSubtitleLeft,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanSubtitleLeft),
                    },
                    new SimpleMenuFlyoutItemModel()
                    {
                        Text = StringResourceProvider.Instance.PanRight,
                        ShortcutAction = KeyboardShortcutActions.PanSubtitleRight,
                        Click = () => context.AdjustPan(KeyboardShortcutActions.PanSubtitleRight),
                    },
                },
            },
        ];
    }

    private static List<BaseMenuFlyoutItemModel> CreateSubtitleTrackItems(PlayerMenuContext context)
    {
        bool allowMulti = context.AllowMultiSubtitleTracks;

        List<BaseMenuFlyoutItemModel> items =
        [
            new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.AddSubtitleTracks,
                Click = context.AddSubtitleTracks,
            },
            new SeparatorMenuFlyoutItemModel(),
        ];

        IReadOnlyList<MediaStreamInfo>? streams = context.Tracks(MediaStreamKind.Subtitle);
        if (streams is null || streams.Count == 0)
        {
            items.Add(new SimpleMenuFlyoutItemModel() { Text = StringResourceProvider.Instance.None, IsEnabled = false });
            return items;
        }

        bool playing = streams.Any(context.IsTrackPlaying);

        foreach (MediaStreamInfo stream in streams)
        {
            MediaStreamInfo choice = stream;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = MediaStreamLabel.Describe(choice, context.SourceOf(choice)),
                IsChecked = context.IsTrackPlaying(choice),
                Click = () =>
                {
                    if (allowMulti)
                    {
                        context.ToggleSubtitleTrack(choice);
                    }
                    else
                    {
                        context.SelectTrack(MediaStreamKind.Subtitle, choice);
                    }
                },
            });
        }

        items.Add(new SeparatorMenuFlyoutItemModel());
        items.Add(new ToggleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.AllowMultiTracks,
            IsChecked = allowMulti,
            Click = () => context.SetAllowMultiSubtitleTracks(!allowMulti),
        });
        items.Add(new ToggleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Off,
            IsChecked = !playing,
            Click = () =>
            {
                if (allowMulti)
                {
                    context.ClearSubtitleTracks();
                }
                else
                {
                    context.SelectTrack(MediaStreamKind.Subtitle, null);
                }
            },
        });

        return items;
    }

    private static List<PlaybackComponentOption> GetSubtitleFontFamilyOptions()
    {
        List<PlaybackComponentOption> options = [new(string.Empty, StringResourceProvider.Instance.Default)];
        foreach (string family in SubtitleFonts.GetFamilies())
        {
            options.Add(new(family, family));
        }

        return options;
    }

    private static List<PlaybackComponentOption> GetSubtitleFontStyleOptions()
    {
        List<PlaybackComponentOption> options = [];
        foreach (FontStyle style in Enum.GetValues<FontStyle>())
        {
            string label = style switch
            {
                FontStyle.Oblique => StringResourceProvider.Instance.FontStyleOblique,
                FontStyle.Italic => StringResourceProvider.Instance.FontStyleItalic,
                _ => StringResourceProvider.Instance.FontStyleNormal,
            };
            options.Add(new(style.ToString(), label));
        }

        return options;
    }

    private static List<PlaybackComponentOption> GetSubtitleFontWeightOptions(string family)
    {
        List<PlaybackComponentOption> options = [];
        foreach (int weight in SubtitleFonts.GetWeights(family))
        {
            string number = weight.ToString(CultureInfo.CurrentCulture);
            string label = weight == PlayerSession.DefaultSubtitleFontWeight ? $"{number} ({StringResourceProvider.Instance.Default})" : number;
            options.Add(new(weight.ToString(CultureInfo.InvariantCulture), label));
        }

        return options;
    }

    private static List<PlaybackComponentOption> GetSubtitleAlignmentOptions()
    {
        return
        [
            new(string.Empty, StringResourceProvider.Instance.Default),
            new(nameof(SubtitleTextAlignment.BottomLeft), StringResourceProvider.Instance.AlignmentBottomLeft),
            new(nameof(SubtitleTextAlignment.BottomCenter), StringResourceProvider.Instance.AlignmentBottomCenter),
            new(nameof(SubtitleTextAlignment.BottomRight), StringResourceProvider.Instance.AlignmentBottomRight),
            new(nameof(SubtitleTextAlignment.MiddleLeft), StringResourceProvider.Instance.AlignmentMiddleLeft),
            new(nameof(SubtitleTextAlignment.MiddleCenter), StringResourceProvider.Instance.AlignmentMiddleCenter),
            new(nameof(SubtitleTextAlignment.MiddleRight), StringResourceProvider.Instance.AlignmentMiddleRight),
            new(nameof(SubtitleTextAlignment.TopLeft), StringResourceProvider.Instance.AlignmentTopLeft),
            new(nameof(SubtitleTextAlignment.TopCenter), StringResourceProvider.Instance.AlignmentTopCenter),
            new(nameof(SubtitleTextAlignment.TopRight), StringResourceProvider.Instance.AlignmentTopRight),
        ];
    }

    private static string GetSubtitleFontWeightKey(int weight)
    {
        return (weight > 0 ? weight : PlayerSession.DefaultSubtitleFontWeight).ToString(CultureInfo.InvariantCulture);
    }

    private static List<BaseMenuFlyoutItemModel> CreateSyncMenuItems(PlayerMenuContext context, MediaStreamKind kind)
    {
        (string forward, string backward) = kind switch
        {
            MediaStreamKind.Video => (KeyboardShortcutActions.SyncVideoForward, KeyboardShortcutActions.SyncVideoBackward),
            MediaStreamKind.Audio => (KeyboardShortcutActions.SyncAudioForward, KeyboardShortcutActions.SyncAudioBackward),
            _ => (KeyboardShortcutActions.SyncSubtitleForward, KeyboardShortcutActions.SyncSubtitleBackward),
        };

        return
        [
            new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SyncForward,
                ShortcutAction = forward,
                Click = () => context.AdjustSync(kind, true),
            },
            new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SyncBackward,
                ShortcutAction = backward,
                Click = () => context.AdjustSync(kind, false),
            },
        ];
    }

    private static SubItemMenuFlyoutItemModel CreateStretchModeSubItem(string text, VideoResizeMode resizeMode, VideoSettingsModel settings, Action<VideoResizeMode, double> select)
    {
        List<BaseMenuFlyoutItemModel> items =
        [
            new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Default,
                IsChecked = settings.ResizeMode == resizeMode && settings.AspectRatio == 0.0,
                Click = () => select(resizeMode, 0.0),
            },
        ];

        foreach ((string key, double value) in AspectRatioOptions)
        {
            double aspectRatio = value;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = key,
                IsChecked = settings.ResizeMode == resizeMode && Math.Abs(settings.AspectRatio - aspectRatio) < 0.001,
                Click = () => select(resizeMode, aspectRatio),
            });
        }

        return new SubItemMenuFlyoutItemModel()
        {
            Text = text,
            Items = items,
        };
    }

    private static string? GetAspectRatioKey(double aspectRatio)
    {
        foreach ((string key, double value) in AspectRatioOptions)
        {
            if (Math.Abs(value - aspectRatio) < 0.001)
            {
                return key;
            }
        }

        return null;
    }

    private static void OpenInNewWindow(PlayerMenuContext context)
    {
        if (string.IsNullOrEmpty(context.Url))
        {
            return;
        }

        ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
            .AddParameter(OpenTabProvider.PARAM_URL, context.Url)
            .AddParameter(OpenTabProvider.PARAM_WINDOW_ID, OpenTabProvider.WINDOW_ID_NEW)
            .Build();
        context.ActionHandler.HandleNoResult(actionModel);
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
