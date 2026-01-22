using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HyLordServerUtil
{
    public partial class ModConfigEditorWindow : Window
    {
        private readonly string configPath;
        private JsonNode? rootNode;

        private readonly List<GroupVm> groups = new();
        private readonly List<ConfigEntryVm> allEntries = new();

        private string currentGroup = "";
        private List<ConfigEntryVm> currentGroupEntries = new();


        public ModConfigEditorWindow(string modDisplayName, string configPath)
        {
            InitializeComponent();
            this.configPath = configPath;

            TitleText.Text = $"{modDisplayName} Config";
            PathText.Text = configPath;

            LoadFile();
        }

        private void LoadFile()
        {
            if (!File.Exists(configPath))
            {
                MessageBox.Show("Config file not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            rootNode = JsonNode.Parse(File.ReadAllText(configPath));
            BuildViewModels();

            GroupList.ItemsSource = groups;
            GroupList.SelectedIndex = groups.Count > 0 ? 0 : -1;

            UpdateDirtyIndicator();
        }

        private void BuildViewModels()
        {
            groups.Clear();
            allEntries.Clear();

            if (rootNode is JsonObject obj)
            {
                foreach (var kv in obj)
                {
                    groups.Add(new GroupVm(kv.Key));
                    FlattenNode(kv.Key, kv.Key, kv.Value);
                }
            }
            else
            {
                groups.Add(new GroupVm("(root)"));
                FlattenNode("(root)", "", rootNode);
            }
        }

        private void FlattenNode(string group, string path, JsonNode? node)
        {
            if (node == null) return;

            if (node is JsonObject o)
            {
                foreach (var kv in o)
                {
                    var childPath = string.IsNullOrWhiteSpace(path) ? kv.Key : $"{path}.{kv.Key}";
                    FlattenNode(group, childPath, kv.Value);
                }
                return;
            }

            if (node is JsonArray)
            {
                allEntries.Add(new ConfigEntryVm
                {
                    Group = group,
                    Key = KeyFromPath(path),
                    Path = path,
                    Kind = ConfigValueKind.Json,
                    ValueText = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    OriginalNode = node
                });
                return;
            }

            if (node is JsonValue v)
            {
                if (TryGetBool(v, out var b))
                {
                    allEntries.Add(new ConfigEntryVm
                    {
                        Group = group,
                        Key = KeyFromPath(path),
                        Path = path,
                        Kind = ConfigValueKind.Bool,
                        BoolValue = b,
                        OriginalNode = node
                    });
                    return;
                }

                if (TryGetNumberText(v, out var numText))
                {
                    allEntries.Add(new ConfigEntryVm
                    {
                        Group = group,
                        Key = KeyFromPath(path),
                        Path = path,
                        Kind = ConfigValueKind.Number,
                        ValueText = numText,
                        OriginalNode = node
                    });
                    return;
                }

                var s = TryGetString(v);
                if (s != null)
                {
                    allEntries.Add(new ConfigEntryVm
                    {
                        Group = group,
                        Key = KeyFromPath(path),
                        Path = path,
                        Kind = (s.Length > 80 || s.Contains('\n')) ? ConfigValueKind.MultilineString : ConfigValueKind.String,
                        ValueText = s,
                        OriginalNode = node
                    });
                    return;
                }

                allEntries.Add(new ConfigEntryVm
                {
                    Group = group,
                    Key = KeyFromPath(path),
                    Path = path,
                    Kind = ConfigValueKind.Json,
                    ValueText = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    OriginalNode = node
                });
                return;
            }

            allEntries.Add(new ConfigEntryVm
            {
                Group = group,
                Key = KeyFromPath(path),
                Path = path,
                Kind = ConfigValueKind.Json,
                ValueText = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                OriginalNode = node
            });
        }

        private static string GetParentWithinGroup(string groupName, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return "(root)";

            var segs = fullPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0)
                return "(root)";

            if (segs.Length >= 2 && segs[0].Equals(groupName, StringComparison.OrdinalIgnoreCase))
                return segs[1];

            if (segs.Length >= 2)
                return segs[0];

            return "(root)";
        }

        private List<ParentGroupVm> BuildParentGroups(IEnumerable<ConfigEntryVm> entries, string groupName, string query)
        {
            var tokens = (query ?? "")
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .ToArray();

            bool Matches(ConfigEntryVm vm)
            {
                if (tokens.Length == 0) return true;

                var hay = $"{vm.Key} {vm.Path} {vm.TypeName}".ToLowerInvariant();
                return tokens.All(t => hay.Contains(t));
            }

            var filtered = entries.Where(Matches);

            var grouped = filtered
                .GroupBy(e => GetParentWithinGroup(groupName, e.Path), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key.Equals("(root)", StringComparison.OrdinalIgnoreCase) ? "~~~~" : g.Key) // root last
                .Select(g => new ParentGroupVm
                {
                    Name = g.Key,
                    Entries = g.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                    IsExpanded = true
                })
                .ToList();

            return grouped;
        }





        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearchFilter();
        }
        private void ApplySearchFilter()
        {
            if (currentGroupEntries == null) return;

            var q = SearchBox?.Text ?? "";
            bool useParents = currentGroupEntries.Any(e => HasParentWithinGroup(currentGroup, e.Path));

            if (!useParents)
            {
                FlatEntryList.ItemsSource = FilterFlat(currentGroupEntries, q);
                FlatEntryList.Visibility = Visibility.Visible;

                ParentEntryList.ItemsSource = null;
                ParentEntryList.Visibility = Visibility.Collapsed;
                return;
            }

            ParentEntryList.ItemsSource = BuildParentGroups(currentGroupEntries, currentGroup, q);
            ParentEntryList.Visibility = Visibility.Visible;

            FlatEntryList.ItemsSource = null;
            FlatEntryList.Visibility = Visibility.Collapsed;
        }




        private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GroupList.SelectedItem is not GroupVm g) return;

            currentGroup = g.Name;
            currentGroupEntries = allEntries.Where(x => x.Group == g.Name).ToList();

            ApplySearchFilter();
        }
        private static bool HasParentWithinGroup(string groupName, string fullPath)
        {
            var segs = (fullPath ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
            return segs.Length >= 3 && segs[0].Equals(groupName, StringComparison.OrdinalIgnoreCase);
        }
        private List<ConfigEntryVm> FilterFlat(IEnumerable<ConfigEntryVm> entries, string query)
        {
            var tokens = (query ?? "")
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .ToArray();

            bool Matches(ConfigEntryVm vm)
            {
                if (tokens.Length == 0) return true;
                var hay = $"{vm.Key} {vm.Path} {vm.TypeName}".ToLowerInvariant();
                return tokens.All(t => hay.Contains(t));
            }

            return entries
                .Where(Matches)
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }




        private void Reload_Click(object sender, RoutedEventArgs e) => LoadFile();
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (rootNode == null) return;

            try
            {
                foreach (var entry in allEntries)
                    ApplyEntry(rootNode, entry);

                File.WriteAllText(configPath, rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        private static void ApplyEntry(JsonNode root, ConfigEntryVm entry)
        {
            var segs = entry.Path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) return;

            JsonNode? cur = root;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (cur is JsonObject o) cur = o[segs[i]];
                else return;
            }

            if (cur is not JsonObject parent) return;
            var key = segs[^1];

            switch (entry.Kind)
            {
                case ConfigValueKind.Bool:
                    parent[key] = entry.BoolValue;
                    return;

                case ConfigValueKind.Number:
                    if (long.TryParse(entry.ValueText, out var l)) parent[key] = l;
                    else if (double.TryParse(entry.ValueText, out var d)) parent[key] = d;
                    else throw new Exception($"Invalid number for {entry.Path}: {entry.ValueText}");
                    return;

                case ConfigValueKind.Json:
                    {
                        var parsed = JsonNode.Parse(entry.ValueText);
                        parent[key] = parsed;
                        return;
                    }

                default:
                    parent[key] = entry.ValueText ?? "";
                    return;
            }
        }

        private void EditRawJson_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ConfigEntryVm vm) return;

            var dialog = new RawJsonEditorWindow(vm.Key, vm.ValueText) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                vm.ValueText = dialog.JsonText;
                MarkDirty(vm);

                if (ParentEntryList != null && ParentEntryList.Visibility == Visibility.Visible)
                    ParentEntryList.Items.Refresh();

                if (FlatEntryList != null && FlatEntryList.Visibility == Visibility.Visible)
                    FlatEntryList.Items.Refresh();
            }
        }

        private void Number_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(ch => !(char.IsDigit(ch) || ch == '-' || ch == '.'));
        }

        private void MarkDirty(ConfigEntryVm vm)
        {
            vm.IsDirty = true;
            UpdateDirtyIndicator();
        }

        private void UpdateDirtyIndicator()
        {
            DirtyText.Text = allEntries.Any(x => x.IsDirty) ? "Unsaved changes" : "";
        }

        private static string KeyFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "(root)";
            var lastDot = path.LastIndexOf('.');
            return lastDot >= 0 ? path[(lastDot + 1)..] : path;
        }

        private static bool TryGetBool(JsonValue v, out bool b)
        {
            try { b = v.GetValue<bool>(); return true; }
            catch { b = false; return false; }
        }

        private static bool TryGetNumberText(JsonValue v, out string text)
        {
            try
            {
                var el = v.GetValue<JsonElement>();
                if (el.ValueKind == JsonValueKind.Number)
                {
                    text = el.GetRawText();
                    return true;
                }
            }
            catch { }
            text = "";
            return false;
        }

        private static string? TryGetString(JsonValue v)
        {
            try { return v.GetValue<string>(); } catch { return null; }
        }
    }
}
