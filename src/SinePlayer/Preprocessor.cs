// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer;

/// <summary>
/// Use to ensure all the preprocessor macros are properly defined.
/// </summary>
internal class Preprocessor
{
#if x64 || ARM64
#else
#error Architecture not defined.
#endif
}
