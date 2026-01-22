using System;

namespace HyLordServerUtil
{
    public class BackupInfo
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public DateTime CreatedLocal { get; set; }
        public long SizeBytes { get; set; }

        public string SizeText =>
            SizeBytes >= 1024L * 1024L * 1024L ? $"{SizeBytes / (1024d * 1024d * 1024d):0.00} GB" :
            SizeBytes >= 1024L * 1024L ? $"{SizeBytes / (1024d * 1024d):0.00} MB" :
            SizeBytes >= 1024L ? $"{SizeBytes / 1024d:0.0} KB" :
            $"{SizeBytes} B";

        public string CreatedText => CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss");
    }
}