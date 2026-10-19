// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Common.Localization;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.ServiceManagement.Services;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Misc;
using SinePlayer.SDK.Models;

namespace SinePlayer.Common.Services;

internal class DebugService : IDebugService
{
    public bool SentryEnabled => AppSettingsModel.SendUsageData;

    public void OnCrashReport(string info)
    {
        DialogOptions options = new DialogOptions.Builder()
            .SetTitle(StringResourceProvider.Instance.UnhandledExceptionTitle)
            .SetContent(StringResourceProvider.Instance.UnhandledExceptionContent.Replace("$info", info), selectable: true)
            .SetPrimaryButtonText(StringResourceProvider.Instance.OK)
            .SetSecondaryButtonText(StringResourceProvider.Instance.Copy)
            .OnSecondaryButtonClick((e) =>
            {
                ClipboardUtils.SetText(info);
                e.Cancel = true;
            })
            .Build();
        CoroutineUtils.Run(() => DialogUtils.EnqueueDialogAsync(options));
    }
}
