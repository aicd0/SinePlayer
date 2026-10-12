// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

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
