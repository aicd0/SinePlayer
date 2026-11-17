// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

// Licensed to the Microsoft Corporation under one or more agreements.
// The Microsoft Corporation licenses this file to you under the MIT license.

#nullable disable

using System.Management;

namespace SinePlayer.Core.Common.AppEnvironment;

internal class ManagementClassFactory
{
    private const string WmiScope = @"root\WMI";

    private static ManagementClassFactory _instanceField;

    //
    // Summary:
    //     Gets or sets the shared instance of ManagementClassFactory. Should never return
    //     null.
    internal static ManagementClassFactory Instance => _instanceField ??= new ManagementClassFactory();

    private ManagementClassFactory()
    {
    }

    public ManagementClass GetComputerSystemClass()
    {
        return new ManagementClass("Win32_ComputerSystem");
    }

    public ManagementClass GetOperatingSystemClass()
    {
        return new ManagementClass("Win32_OperatingSystem");
    }

    public ManagementClass GetWmiMonitorBrightnessClass()
    {
        return new ManagementClass(WmiScope, "WmiMonitorBrightness", null);
    }

    public ManagementClass GetWmiMonitorBrightnessMethodsClass()
    {
        return new ManagementClass(WmiScope, "WmiMonitorBrightnessMethods", null);
    }
}
