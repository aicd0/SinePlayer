// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Core.Database.JSON;

public interface IConfigBackingLayer
{
    string? ReadConfig();

    void WriteConfig(string value);
}
