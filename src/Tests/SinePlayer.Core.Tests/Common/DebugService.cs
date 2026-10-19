// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ServiceManagement.Services;

namespace SinePlayer.Core.Tests.Common;

internal class DebugService : IDebugService
{
    public bool SentryEnabled => false;

    public void OnCrashReport(string info)
    {
        Assert.Fail(info);
    }
}
