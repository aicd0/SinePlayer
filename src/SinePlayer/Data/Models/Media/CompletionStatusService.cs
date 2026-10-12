// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using SinePlayer.Common.Localization;

namespace SinePlayer.Data.Models.Media;

internal static class CompletionStatusService
{
    public static readonly IReadOnlyList<CompletionStatusEnum> AllStatus = [
        CompletionStatusEnum.Abandoned,
        CompletionStatusEnum.OnHold,
        CompletionStatusEnum.NotStarted,
        CompletionStatusEnum.Planned,
        CompletionStatusEnum.Ongoing,
        CompletionStatusEnum.Completed,
    ];

    public static int EnumToComparable(CompletionStatusEnum state)
    {
        return state switch
        {
            CompletionStatusEnum.Abandoned => 1,
            CompletionStatusEnum.OnHold => 2,
            CompletionStatusEnum.NotStarted => 3,
            CompletionStatusEnum.Planned => 4,
            CompletionStatusEnum.Ongoing => 5,
            CompletionStatusEnum.Completed => 6,
            _ => 0,
        };
    }

    public static string EnumToString(CompletionStatusEnum status, string? fallback = null)
    {
        return status switch
        {
            CompletionStatusEnum.NotStarted => StringResourceProvider.Instance.CompletionStatusNotStarted,
            CompletionStatusEnum.Ongoing => StringResourceProvider.Instance.CompletionStatusOngoing,
            CompletionStatusEnum.Completed => StringResourceProvider.Instance.CompletionStatusCompleted,
            CompletionStatusEnum.Abandoned => StringResourceProvider.Instance.CompletionStatusAbandoned,
            CompletionStatusEnum.Planned => StringResourceProvider.Instance.CompletionStatusPlanned,
            CompletionStatusEnum.OnHold => StringResourceProvider.Instance.CompletionStatusOnHold,
            _ => fallback is null ? throw new ArgumentException($"Unknwon completion status '{status}'.") : fallback,
        };
    }

    public static SDK.Plugins.Media.CompletionStatusEnum HostEnumToSDKEnum(CompletionStatusEnum status)
    {
        return status switch
        {
            CompletionStatusEnum.NotStarted => SDK.Plugins.Media.CompletionStatusEnum.NotStarted,
            CompletionStatusEnum.Ongoing => SDK.Plugins.Media.CompletionStatusEnum.Ongoing,
            CompletionStatusEnum.Completed => SDK.Plugins.Media.CompletionStatusEnum.Completed,
            CompletionStatusEnum.Abandoned => SDK.Plugins.Media.CompletionStatusEnum.Abandoned,
            CompletionStatusEnum.Planned => SDK.Plugins.Media.CompletionStatusEnum.Planned,
            CompletionStatusEnum.OnHold => SDK.Plugins.Media.CompletionStatusEnum.OnHold,
            _ => throw new ArgumentException($"Unknwon completion status '{status}'."),
        };
    }

    public static CompletionStatusEnum SDKEnumToHostEnum(SDK.Plugins.Media.CompletionStatusEnum status)
    {
        return status switch
        {
            SDK.Plugins.Media.CompletionStatusEnum.NotStarted => CompletionStatusEnum.NotStarted,
            SDK.Plugins.Media.CompletionStatusEnum.Ongoing => CompletionStatusEnum.Ongoing,
            SDK.Plugins.Media.CompletionStatusEnum.Completed => CompletionStatusEnum.Completed,
            SDK.Plugins.Media.CompletionStatusEnum.Abandoned => CompletionStatusEnum.Abandoned,
            SDK.Plugins.Media.CompletionStatusEnum.Planned => CompletionStatusEnum.Planned,
            SDK.Plugins.Media.CompletionStatusEnum.OnHold => CompletionStatusEnum.OnHold,
            _ => throw new ArgumentException($"Unknwon completion status '{status}'."),
        };
    }

    public static bool CanTransitToOngoingAutomatically(CompletionStatusEnum status)
    {
        return status switch
        {
            CompletionStatusEnum.NotStarted => true,
            CompletionStatusEnum.Ongoing => false,
            CompletionStatusEnum.Completed => false,
            CompletionStatusEnum.Abandoned => false,
            CompletionStatusEnum.Planned => true,
            CompletionStatusEnum.OnHold => false,
            _ => false,
        };
    }
}
