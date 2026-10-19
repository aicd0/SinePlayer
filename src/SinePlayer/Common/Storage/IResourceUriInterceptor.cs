// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Diagnostics.CodeAnalysis;

namespace SinePlayer.Common.Storage;

internal interface IResourceUriInterceptor
{
    bool TryParse(Uri uri, [NotNullWhen(true)] out IResourceUriHandler? handler);
}
