using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace RotationTracker.Services
{
    public sealed class OperatorInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public Dictionary<string, string> SkillIcons { get; set; } = new Dictionary<string, string>();

        public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Name) ? (Id ?? "") : Name;
    }

    /// <summary>
    /// Loads the Endfield operator/skill catalog from the packaged
    /// <c>Assets/operators.json</c> file and resolves icon URLs.
    /// </summary>
    public sealed class OperatorCatalog
    {
        private static readonly Lazy<OperatorCatalog> _instance =
            new Lazy<OperatorCatalog>(() => new OperatorCatalog());

        public static OperatorCatalog Instance => _instance.Value;

        private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
        private bool _loaded;
        private List<OperatorInfo> _operators = new List<OperatorInfo>();
        private Dictionary<string, OperatorInfo> _byId = new Dictionary<string, OperatorInfo>(StringComparer.OrdinalIgnoreCase);

        private OperatorCatalog() { }

        public IReadOnlyList<OperatorInfo> All => _operators;

        public OperatorInfo Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            _byId.TryGetValue(id, out var info);
            return info;
        }

        public string GetAvatarUrl(string id)
        {
            return string.IsNullOrWhiteSpace(Get(id)?.AvatarUrl) ? null : Get(id).AvatarUrl;
        }

        public string GetSkillIconUrl(string operatorId, string skillSlot)
        {
            var op = Get(operatorId);
            if (op == null || string.IsNullOrEmpty(skillSlot)) return null;
            if (op.SkillIcons.TryGetValue(skillSlot, out var url) && !string.IsNullOrWhiteSpace(url)) return url;
            return null;
        }

        public async Task EnsureLoadedAsync()
        {
            if (_loaded) return;
            await _loadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_loaded) return;

                try
                {
                    var file = await StorageFile.GetFileFromApplicationUriAsync(
                        new Uri("ms-appx:///Assets/operators.json"));
                    var json = await FileIO.ReadTextAsync(file);
                    Parse(json);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[OperatorCatalog] Load failed: {ex.Message}");
                    _operators = new List<OperatorInfo>();
                    _byId = new Dictionary<string, OperatorInfo>(StringComparer.OrdinalIgnoreCase);
                }

                _loaded = true;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        private void Parse(string json)
        {
            var list = new List<OperatorInfo>();
            var map = new Dictionary<string, OperatorInfo>(StringComparer.OrdinalIgnoreCase);

            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var op = new OperatorInfo { Id = prop.Name };

                    if (prop.Value.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        op.Name = nameEl.GetString() ?? "";
                    if (prop.Value.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String)
                        op.Slug = slugEl.GetString() ?? "";
                    if (prop.Value.TryGetProperty("avatar", out var avatarEl) && avatarEl.ValueKind == JsonValueKind.String)
                        op.AvatarUrl = avatarEl.GetString() ?? "";

                    if (prop.Value.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var skillProp in skillsEl.EnumerateObject())
                        {
                            if (skillProp.Value.ValueKind != JsonValueKind.Object) continue;

                            // Each slot holds a dictionary of icon_id -> url. We only need
                            // the first URL to render the skill icon.
                            foreach (var iconProp in skillProp.Value.EnumerateObject())
                            {
                                if (iconProp.Value.ValueKind == JsonValueKind.String)
                                {
                                    op.SkillIcons[skillProp.Name] = iconProp.Value.GetString() ?? "";
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(op.AvatarUrl))
                    {
                        op.AvatarUrl = null;
                    }

                    foreach (var key in op.SkillIcons.Keys.ToList())
                    {
                        if (string.IsNullOrWhiteSpace(op.SkillIcons[key]))
                        {
                            op.SkillIcons.Remove(key);
                        }
                    }

                    list.Add(op);
                    map[op.Id] = op;
                }
            }

            _operators = list
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _byId = map;
        }
    }
}
