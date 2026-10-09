// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.Core.Common.ServiceManagement.Services;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.SDK.Models;

namespace SinePlayerUWP.Common.Services;

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
