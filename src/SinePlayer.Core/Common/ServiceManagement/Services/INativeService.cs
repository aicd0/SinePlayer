// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ServiceManagement.Models;

namespace SinePlayer.Core.Common.ServiceManagement.Services;

public interface INativeService : IService
{
    NativeDialogResult ShowDialog(NativeDialogButtonType buttonType, NativeDialogIconType iconType, string caption, string text);
}
