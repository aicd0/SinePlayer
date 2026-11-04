// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public readonly record struct SubtitleChange(TimeSpan Start, TimeSpan End, bool Continuous);
