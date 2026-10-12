// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using SinePlayerUWP.Common.Localization;

namespace SinePlayerUWP.Data.Models.Media;

internal static class CompletionStatusService
{
    public static readonly IReadOnlyList<CompletionStatusEnum> AllStatus = [
        CompletionStatusEnum.Abandoned,
        CompletionStatusEnum.OnHold,
        CompletionStatusEnum.Unread,
        CompletionStatusEnum.PlanToRead,
        CompletionStatusEnum.Reading,
        CompletionStatusEnum.Completed,
    ];

    public static int EnumToComparable(CompletionStatusEnum state)
    {
        return state switch
        {
            CompletionStatusEnum.Abandoned => 1,
            CompletionStatusEnum.OnHold => 2,
            CompletionStatusEnum.Unread => 3,
            CompletionStatusEnum.PlanToRead => 4,
            CompletionStatusEnum.Reading => 5,
            CompletionStatusEnum.Completed => 6,
            _ => 0,
        };
    }

    public static string EnumToString(CompletionStatusEnum status, string? fallback = null)
    {
        return status switch
        {
            CompletionStatusEnum.Unread => StringResourceProvider.Instance.CompletionStatusUnread,
            CompletionStatusEnum.Reading => StringResourceProvider.Instance.CompletionStatusReading,
            CompletionStatusEnum.Completed => StringResourceProvider.Instance.CompletionStatusCompleted,
            CompletionStatusEnum.Abandoned => StringResourceProvider.Instance.CompletionStatusAbandoned,
            CompletionStatusEnum.PlanToRead => StringResourceProvider.Instance.CompletionStatusPlanToRead,
            CompletionStatusEnum.OnHold => StringResourceProvider.Instance.CompletionStatusOnHold,
            _ => fallback is null ? throw new ArgumentException($"Unknwon completion status '{status}'.") : fallback,
        };
    }

    public static SDK.Plugins.Media.CompletionStatusEnum HostEnumToSDKEnum(CompletionStatusEnum status)
    {
        return status switch
        {
            CompletionStatusEnum.Unread => SDK.Plugins.Media.CompletionStatusEnum.Unread,
            CompletionStatusEnum.Reading => SDK.Plugins.Media.CompletionStatusEnum.Reading,
            CompletionStatusEnum.Completed => SDK.Plugins.Media.CompletionStatusEnum.Completed,
            CompletionStatusEnum.Abandoned => SDK.Plugins.Media.CompletionStatusEnum.Abandoned,
            CompletionStatusEnum.PlanToRead => SDK.Plugins.Media.CompletionStatusEnum.PlanToRead,
            CompletionStatusEnum.OnHold => SDK.Plugins.Media.CompletionStatusEnum.OnHold,
            _ => throw new ArgumentException($"Unknwon completion status '{status}'."),
        };
    }

    public static CompletionStatusEnum SDKEnumToHostEnum(SDK.Plugins.Media.CompletionStatusEnum status)
    {
        return status switch
        {
            SDK.Plugins.Media.CompletionStatusEnum.Unread => CompletionStatusEnum.Unread,
            SDK.Plugins.Media.CompletionStatusEnum.Reading => CompletionStatusEnum.Reading,
            SDK.Plugins.Media.CompletionStatusEnum.Completed => CompletionStatusEnum.Completed,
            SDK.Plugins.Media.CompletionStatusEnum.Abandoned => CompletionStatusEnum.Abandoned,
            SDK.Plugins.Media.CompletionStatusEnum.PlanToRead => CompletionStatusEnum.PlanToRead,
            SDK.Plugins.Media.CompletionStatusEnum.OnHold => CompletionStatusEnum.OnHold,
            _ => throw new ArgumentException($"Unknwon completion status '{status}'."),
        };
    }

    public static bool CanTransitToReadingAutomatically(CompletionStatusEnum status)
    {
        return status switch
        {
            CompletionStatusEnum.Unread => true,
            CompletionStatusEnum.Reading => false,
            CompletionStatusEnum.Completed => false,
            CompletionStatusEnum.Abandoned => false,
            CompletionStatusEnum.PlanToRead => true,
            CompletionStatusEnum.OnHold => false,
            _ => false,
        };
    }
}
