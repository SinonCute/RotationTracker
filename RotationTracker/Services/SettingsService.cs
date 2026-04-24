using RotationTracker.Models;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using Windows.Storage;

namespace RotationTracker.Services
{
    /// <summary>
    /// Loads and saves <see cref="RotationSettings"/> via ApplicationData local settings.
    /// Serializes to JSON so the schema remains portable and diff-friendly.
    /// </summary>
    public sealed class SettingsService
    {
        private const string SettingsKey = "RotationSettingsJson";
        private const string SettingsFileName = "rotation-settings.json";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            IncludeFields = false,
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private static readonly Lazy<SettingsService> _instance =
            new Lazy<SettingsService>(() => new SettingsService());

        public static SettingsService Instance => _instance.Value;

        public RotationSettings Current { get; private set; }

        public event EventHandler SettingsChanged;

        private SettingsService()
        {
            Current = Load();
        }

        public RotationSettings Load()
        {
            var fromFile = TryLoadFromFile();
            if (fromFile != null)
            {
                Current = fromFile;
                App.BootstrapLog($"[SettingsService] Loaded from file. rotations={fromFile.Rotations?.Count ?? 0}; actions={SummarizeActions(fromFile)}");
                return fromFile;
            }

            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.TryGetValue(SettingsKey, out var raw) && raw is string json && !string.IsNullOrWhiteSpace(json))
                {
                    if (LooksLikeLegacyPayload(json))
                    {
                        Trace.WriteLine("[SettingsService] Legacy payload detected; resetting to defaults.");
                    }
                    else
                    {
                        var parsed = DeserializeSettings(json);
                        if (parsed != null)
                        {
                            Normalize(parsed);
                            Current = parsed;
                            App.BootstrapLog($"[SettingsService] Loaded from LocalSettings. rotations={parsed.Rotations?.Count ?? 0}; actions={SummarizeActions(parsed)}");
                            return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SettingsService] Load failed: {ex.Message}");
                App.BootstrapLog("[SettingsService] LocalSettings load failed.", ex);
            }

            if (Current != null && Current.Rotations != null && Current.Rotations.Count > 0)
            {
                App.BootstrapLog($"[SettingsService] Falling back to in-memory settings. rotations={Current.Rotations.Count}");
                return Current;
            }

            var defaults = CreateDefaults();
            Current = defaults;
            App.BootstrapLog("[SettingsService] Using defaults.");
            return defaults;
        }

        public void Save(RotationSettings settings)
        {
            if (settings == null) return;
            Normalize(settings);
            Current = settings;

            try
            {
                var json = SerializeSettings(settings);
                TrySaveToFile(json);
                ApplicationData.Current.LocalSettings.Values[SettingsKey] = json;
                App.BootstrapLog($"[SettingsService] Saved. rotations={settings.Rotations?.Count ?? 0}; actions={SummarizeActions(settings)}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SettingsService] Save failed: {ex.Message}");
                App.BootstrapLog("[SettingsService] Save failed.", ex);
            }

            try { SettingsChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        public static string ExportRotation(RotationDefinition rotation)
        {
            if (rotation == null)
            {
                throw new ArgumentNullException(nameof(rotation));
            }

            var document = new RotationDefinitionDocument
            {
                Id = rotation.Id ?? "",
                Name = rotation.Name ?? "",
                OperatorSlots = rotation.OperatorSlots?.ToList() ?? new List<string>(),
            };

            if (rotation.Steps != null)
            {
                foreach (var step in rotation.Steps)
                {
                    if (step == null) continue;
                    document.Steps.Add(new RotationStepDocument
                    {
                        SlotIndex = step.SlotIndex,
                        Action = step.Action,
                        LabelOverride = step.LabelOverride ?? "",
                    });
                }
            }

            var json = JsonSerializer.Serialize(document, JsonOptions);
            return "rtrot1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static bool TryImportRotation(string payload, out RotationDefinition rotation, out string error)
        {
            rotation = null;
            error = null;

            if (string.IsNullOrWhiteSpace(payload))
            {
                error = "Share string is empty.";
                return false;
            }

            try
            {
                var trimmed = payload.Trim();
                string json;

                if (trimmed.StartsWith("rtrot1:", StringComparison.OrdinalIgnoreCase))
                {
                    var base64 = trimmed.Substring("rtrot1:".Length);
                    json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                }
                else
                {
                    json = trimmed;
                }

                var document = JsonSerializer.Deserialize<RotationDefinitionDocument>(json, JsonOptions);
                if (document == null)
                {
                    error = "Share string could not be parsed.";
                    return false;
                }

                rotation = new RotationDefinition
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = document.Name ?? "",
                    OperatorSlots = new ObservableCollection<string>(document.OperatorSlots ?? new List<string>()),
                    Steps = new ObservableCollection<RotationStep>(),
                };

                if (document.Steps != null)
                {
                    foreach (var stepDocument in document.Steps)
                    {
                        if (stepDocument == null) continue;
                        rotation.Steps.Add(new RotationStep
                        {
                            SlotIndex = stepDocument.SlotIndex,
                            Action = stepDocument.Action,
                            LabelOverride = stepDocument.LabelOverride ?? "",
                        });
                    }
                }

                var wrapper = new RotationSettings();
                wrapper.Rotations.Add(rotation);
                Normalize(wrapper);
                rotation = wrapper.Rotations.FirstOrDefault();

                if (rotation == null || rotation.Steps == null || rotation.Steps.Count == 0)
                {
                    error = "Imported rotation has no steps.";
                    return false;
                }

                return true;
            }
            catch (FormatException)
            {
                error = "Share string is not valid base64.";
                return false;
            }
            catch (JsonException)
            {
                error = "Share string is not valid rotation data.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Detect the pre-team model (RotationStep had <c>Key</c>/<c>Label</c> fields,
        /// RotationDefinition had no <c>OperatorSlots</c>).
        /// Uses structural JSON checks instead of substring sniffing so valid
        /// modern payloads are not misclassified and reset to defaults.
        /// </summary>
        private static bool LooksLikeLegacyPayload(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                    if (!TryGetPropertyCaseInsensitive(doc.RootElement, "Rotations", out var rotations)
                        || rotations.ValueKind != JsonValueKind.Array)
                    {
                        return false;
                    }

                    foreach (var rotation in rotations.EnumerateArray())
                    {
                        if (rotation.ValueKind != JsonValueKind.Object) continue;

                        // New schema has OperatorSlots on each rotation object.
                        if (TryGetPropertyCaseInsensitive(rotation, "OperatorSlots", out _)) return false;

                        if (!TryGetPropertyCaseInsensitive(rotation, "Steps", out var steps)
                            || steps.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var step in steps.EnumerateArray())
                        {
                            if (step.ValueKind != JsonValueKind.Object) continue;

                            // Legacy step had Key/Label and no SlotIndex.
                            bool hasLegacyKey = TryGetPropertyCaseInsensitive(step, "Key", out _);
                            bool hasSlotIndex = TryGetPropertyCaseInsensitive(step, "SlotIndex", out _);
                            if (hasLegacyKey && !hasSlotIndex) return true;
                        }
                    }
                }
            }
            catch
            {
                // If payload is malformed, let normal deserialize path handle fallback.
            }

            return false;
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Enforces invariants: each definition has 4 slots, an id, and a steps list.
        /// </summary>
        private static void Normalize(RotationSettings settings)
        {
            if (settings.Rotations == null)
            {
                settings.Rotations = new ObservableCollection<RotationDefinition>();
            }

            foreach (var rotation in settings.Rotations.ToList())
            {
                if (rotation == null)
                {
                    settings.Rotations.Remove(rotation);
                    continue;
                }

                if (string.IsNullOrEmpty(rotation.Id))
                {
                    rotation.Id = Guid.NewGuid().ToString("N");
                }
                rotation.OperatorSlots = RotationDefinition.NormalizeSlots(rotation.OperatorSlots);
                if (rotation.Steps == null)
                {
                    rotation.Steps = new ObservableCollection<RotationStep>();
                }
                else
                {
                    foreach (var step in rotation.Steps)
                    {
                        if (step == null) continue;
                        if (step.SlotIndex < 0) step.SlotIndex = 0;
                        if (step.SlotIndex >= RotationDefinition.SlotCount) step.SlotIndex = RotationDefinition.SlotCount - 1;
                        step.LabelOverride = step.LabelOverride ?? "";
                    }
                }
            }

            if (string.IsNullOrEmpty(settings.ActiveRotationId)
                || settings.Rotations.All(r => r.Id != settings.ActiveRotationId))
            {
                settings.ActiveRotationId = settings.Rotations.FirstOrDefault()?.Id ?? "";
            }

            if (settings.PinnedOpacity < 0.1) settings.PinnedOpacity = 0.1;
            if (settings.PinnedOpacity > 1.0) settings.PinnedOpacity = 1.0;
        }

        public static RotationSettings CreateDefaults()
        {
            var rotation = new RotationDefinition
            {
                Name = "Sample Team",
                OperatorSlots = new ObservableCollection<string>
                {
                    "chr_0002_endminm",
                    "chr_0004_pelica",
                    "chr_0006_wolfgd",
                    "chr_0007_ikut",
                },
                Steps = new ObservableCollection<RotationStep>
                {
                    new RotationStep { SlotIndex = 0, Action = RotationAction.Skill },
                    new RotationStep { SlotIndex = 1, Action = RotationAction.Combo },
                    new RotationStep { SlotIndex = 2, Action = RotationAction.Skill },
                    new RotationStep { SlotIndex = 3, Action = RotationAction.Ultimate },
                    new RotationStep { SlotIndex = 0, Action = RotationAction.Normal },
                },
            };

            var settings = new RotationSettings
            {
                ActiveRotationId = rotation.Id,
                PinnedOpacity = 0.85,
                AutoAdvanceOnKey = true,
                LoopWhenComplete = true,
            };
            settings.Rotations.Add(rotation);
            return settings;
        }

        private static string GetSettingsFilePath()
        {
            try
            {
                var folder = ApplicationData.Current?.LocalFolder?.Path;
                if (string.IsNullOrEmpty(folder)) return null;
                return System.IO.Path.Combine(folder, SettingsFileName);
            }
            catch
            {
                return null;
            }
        }

        private RotationSettings TryLoadFromFile()
        {
            try
            {
                var path = GetSettingsFilePath();
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                var json = System.IO.File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;

                if (LooksLikeLegacyPayload(json))
                {
                    Trace.WriteLine("[SettingsService] Legacy file payload detected; resetting to defaults.");
                    return null;
                }

                var parsed = DeserializeSettings(json);
                if (parsed == null) return null;
                Normalize(parsed);
                return parsed;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SettingsService] File load failed: {ex.Message}");
                App.BootstrapLog("[SettingsService] File load failed.", ex);
                return null;
            }
        }

        private static void TrySaveToFile(string json)
        {
            try
            {
                var path = GetSettingsFilePath();
                if (string.IsNullOrEmpty(path)) return;
                System.IO.File.WriteAllText(path, json ?? "");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SettingsService] File save failed: {ex.Message}");
                App.BootstrapLog("[SettingsService] File save failed.", ex);
            }
        }

        private static RotationSettings DeserializeSettings(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            var parsed = JsonSerializer.Deserialize<RotationSettingsDocument>(json, JsonOptions);
            return parsed != null ? FromDocument(parsed) : null;
        }

        private static string SerializeSettings(RotationSettings settings)
        {
            var document = ToDocument(settings);
            return JsonSerializer.Serialize(document, JsonOptions);
        }

        private static RotationSettingsDocument ToDocument(RotationSettings settings)
        {
            var document = new RotationSettingsDocument
            {
                ActiveRotationId = settings?.ActiveRotationId ?? "",
                PinnedOpacity = settings?.PinnedOpacity ?? 0.85,
                AutoAdvanceOnKey = settings?.AutoAdvanceOnKey ?? true,
                LoopWhenComplete = settings?.LoopWhenComplete ?? true,
            };

            if (settings?.Rotations != null)
            {
                foreach (var rotation in settings.Rotations)
                {
                    if (rotation == null) continue;

                    var rotationDocument = new RotationDefinitionDocument
                    {
                        Id = rotation.Id ?? "",
                        Name = rotation.Name ?? "",
                        OperatorSlots = rotation.OperatorSlots?.ToList() ?? new List<string>(),
                    };

                    if (rotation.Steps != null)
                    {
                        foreach (var step in rotation.Steps)
                        {
                            if (step == null) continue;
                            rotationDocument.Steps.Add(new RotationStepDocument
                            {
                                SlotIndex = step.SlotIndex,
                                Action = step.Action,
                                LabelOverride = step.LabelOverride ?? "",
                            });
                        }
                    }

                    document.Rotations.Add(rotationDocument);
                }
            }

            return document;
        }

        private static RotationSettings FromDocument(RotationSettingsDocument document)
        {
            var settings = new RotationSettings
            {
                ActiveRotationId = document?.ActiveRotationId ?? "",
                PinnedOpacity = document?.PinnedOpacity ?? 0.85,
                AutoAdvanceOnKey = document?.AutoAdvanceOnKey ?? true,
                LoopWhenComplete = document?.LoopWhenComplete ?? true,
            };

            if (document?.Rotations != null)
            {
                foreach (var rotationDocument in document.Rotations)
                {
                    if (rotationDocument == null) continue;

                    var rotation = new RotationDefinition
                    {
                        Id = rotationDocument.Id ?? "",
                        Name = rotationDocument.Name ?? "",
                        OperatorSlots = new ObservableCollection<string>(
                            rotationDocument.OperatorSlots ?? new List<string>()),
                        Steps = new ObservableCollection<RotationStep>(),
                    };

                    if (rotationDocument.Steps != null)
                    {
                        foreach (var stepDocument in rotationDocument.Steps)
                        {
                            if (stepDocument == null) continue;
                            rotation.Steps.Add(new RotationStep
                            {
                                SlotIndex = stepDocument.SlotIndex,
                                Action = stepDocument.Action,
                                LabelOverride = stepDocument.LabelOverride ?? "",
                            });
                        }
                    }

                    settings.Rotations.Add(rotation);
                }
            }

            return settings;
        }

        private static string SummarizeActions(RotationSettings settings)
        {
            if (settings?.Rotations == null || settings.Rotations.Count == 0)
            {
                return "(none)";
            }

            return string.Join(" | ", settings.Rotations.Select(rotation =>
            {
                var name = string.IsNullOrWhiteSpace(rotation?.Name) ? "(unnamed)" : rotation.Name;
                var actions = rotation?.Steps == null || rotation.Steps.Count == 0
                    ? "(no-steps)"
                    : string.Join(",", rotation.Steps.Select(step => step?.Action.ToString() ?? "null"));
                return $"{name}:{actions}";
            }));
        }

        private sealed class RotationSettingsDocument
        {
            public List<RotationDefinitionDocument> Rotations { get; set; } =
                new List<RotationDefinitionDocument>();

            public string ActiveRotationId { get; set; } = "";

            public double PinnedOpacity { get; set; } = 0.85;

            public bool AutoAdvanceOnKey { get; set; } = true;

            public bool LoopWhenComplete { get; set; } = true;
        }

        private sealed class RotationDefinitionDocument
        {
            public string Id { get; set; } = "";

            public string Name { get; set; } = "";

            public List<string> OperatorSlots { get; set; } = new List<string>();

            public List<RotationStepDocument> Steps { get; set; } = new List<RotationStepDocument>();
        }

        private sealed class RotationStepDocument
        {
            public int SlotIndex { get; set; }

            public RotationAction Action { get; set; } = RotationAction.Skill;

            public string LabelOverride { get; set; } = "";
        }
    }
}
