using System.IO;

namespace HyLordServerUtil
{
    public class ModInfo
    {
        public string Name { get; set; } = "(Unknown)";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "OK";

        public string FullPath { get; set; } = "";
        public bool IsLoaded { get; set; } = true;
        public string Group { get; set; } = "";
        public string ConfigPath { get; set; } = "";
        public bool HasConfig => !string.IsNullOrWhiteSpace(ConfigPath) && File.Exists(ConfigPath);

        public string ActionLabel => IsLoaded ? "Unload" : "Load";
    }
}