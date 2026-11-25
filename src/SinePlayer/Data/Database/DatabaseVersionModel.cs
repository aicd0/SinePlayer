// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.IO;
using System.Text.Json.Serialization;

using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Database.JSON;

namespace SinePlayer.Data.Database;

internal static class DatabaseVersionModel
{
    public const int VERSION = 0;

    private static readonly string _versionFilePath = Path.Combine(StorageLocation.LocalFolderPath, "Versions.json");
    private static readonly JsonDatabase _db = new();

    public static int Version
    {
        get
        {
            return _db.Read(model => model.Version ?? 0);
        }
        set
        {
            _db.Write(model => model.Version = value);
            _db.Save();
        }
    }

    private class JsonDatabase : JsonDatabase<JsonModel>
    {
        public JsonDatabase() : base(new FileLayer(_versionFilePath)) { }

        protected override JsonModel InitializeModel(JsonModel? model)
        {
            if (model is null)
            {
                return new()
                {
                    Version = VERSION,
                };
            }

            return model;
        }
    }

    public class JsonModel
    {
        [JsonPropertyName("Version")]
        public int? Version { get; set; }
    }
}
