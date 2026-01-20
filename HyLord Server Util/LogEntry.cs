public enum LogType { Info, Warning, Error }

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Message { get; set; }
    public LogType Type { get; set; }
}