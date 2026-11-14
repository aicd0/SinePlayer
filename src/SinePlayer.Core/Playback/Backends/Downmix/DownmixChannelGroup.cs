// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.Downmix;

/// <summary>
/// One group of speakers that the fold treats as one: the label a mixing UI shows for it, the key the listener's level
/// for it is stored under, and the level it carries until that level is changed, in percent of full scale.
/// </summary>
public readonly record struct DownmixChannelGroup(string Key, string Label, int DefaultLevel);
