using System;
using System.ComponentModel;

namespace HyLordServerUtil
{
    public class PlayerInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = "";
        public string Hash { get; set; } = "";

        public DateTime JoinedAt { get; set; } = DateTime.Now;

        public string SessionLength =>
            (DateTime.Now - JoinedAt).ToString(@"hh\:mm\:ss");

        public void Tick()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionLength)));
        }
    }
}