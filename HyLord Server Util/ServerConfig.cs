using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyLordServerUtil
{
    public class ServerConfig
    {

        public JsonNode? GetNode(string key) => Root[key];
        public JsonObject Root { get; private set; }
        public void SetNode(string key, JsonNode? node) => Root[key] = node;

        public static ServerConfig Load(string path)
        {
            var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return new ServerConfig { Root = json };
        }

        public void Save(string path)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            File.WriteAllText(path, Root.ToJsonString(options));
        }

        public string GetString(string key) =>
            Root[key]?.GetValue<string>() ?? "";

        public void SetString(string key, string value) =>
            Root[key] = value;

        public int GetInt(string key) =>
            Root[key]?.GetValue<int>() ?? 0;

        public void SetInt(string key, int value) =>
            Root[key] = value;

        public bool GetBool(string key) =>
            Root[key]?.GetValue<bool>() ?? false;

        public void SetBool(string key, bool value) =>
            Root[key] = value;

        public JsonObject GetObject(string key)
        {
            if (Root[key] is not JsonObject obj)
            {
                obj = new JsonObject();
                Root[key] = obj;
            }
            return obj;
        }
    }
}