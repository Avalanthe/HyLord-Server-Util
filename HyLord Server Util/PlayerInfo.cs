using System;
using System.ComponentModel;

namespace HyLordServerUtil
{
    public class PlayerInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string name = "";
        public string Name
        {
            get => name;
            set { if (name == value) return; name = value; OnChanged(nameof(Name)); }
        }

        private string hash = "";
        public string Hash
        {
            get => hash;
            set { if (hash == value) return; hash = value; OnChanged(nameof(Hash)); }
        }

        private bool isOnline = true;
        public bool IsOnline
        {
            get => isOnline;
            set { if (isOnline == value) return; isOnline = value; OnChanged(nameof(IsOnline)); }
        }

        private bool isOp = false;
        public bool IsOp
        {
            get => isOp;
            set { if (isOp == value) return; isOp = value; OnChanged(nameof(IsOp)); }
        }

        public DateTime JoinedAt { get; set; } = DateTime.Now;
        public DateTime? LastSeenAt { get; set; }

        public string SessionLength =>
            IsOnline ? (DateTime.Now - JoinedAt).ToString(@"hh\:mm\:ss") : "--:--:--";

        public void Tick()
        {
            if (IsOnline)
                OnChanged(nameof(SessionLength));
        }

        private void OnChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}