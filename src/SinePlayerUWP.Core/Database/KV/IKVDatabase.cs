// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Core.Database.KV;

public interface IKVDatabase : IDisposable
{
    IKVCollection GetCollection(string name);
}
