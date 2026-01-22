using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace HyLordServerUtil
{
    public enum ConfigValueKind
    {
        Bool,
        Number,
        String,
        MultilineString,
        Json
    }

    public class ConfigEntryVm
    {
        public string Group { get; set; } = "";
        public string Key { get; set; } = "";
        public string Path { get; set; } = "";
        public ConfigValueKind Kind { get; set; }

        public string TypeName => Kind.ToString();

        public string ValueText { get; set; } = "";

        public bool BoolValue { get; set; }

        public JsonNode? OriginalNode { get; set; }

        public bool IsDirty { get; set; }
    }

    public class GroupVm
    {
        public string Name { get; }
        public GroupVm(string name) => Name = name;
        public override string ToString() => Name;
    }

    public class ConfigValueTemplateSelector : DataTemplateSelector
    {
        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is not ConfigEntryVm vm) return base.SelectTemplate(item, container);

            var fe = container as FrameworkElement;
            if (fe == null) return base.SelectTemplate(item, container);

            return vm.Kind switch
            {
                ConfigValueKind.Bool => (DataTemplate)fe.FindResource("BoolTemplate"),
                ConfigValueKind.Number => (DataTemplate)fe.FindResource("NumberTemplate"),
                ConfigValueKind.MultilineString => (DataTemplate)fe.FindResource("MultilineStringTemplate"),
                ConfigValueKind.Json => (DataTemplate)fe.FindResource("JsonTemplate"),
                _ => (DataTemplate)fe.FindResource("StringTemplate"),
            };
        }
    }
    public sealed class ParentGroupVm
    {
        public string Name { get; set; } = "";
        public List<ConfigEntryVm> Entries { get; set; } = new();
        public bool IsExpanded { get; set; } = true;
    }






}
