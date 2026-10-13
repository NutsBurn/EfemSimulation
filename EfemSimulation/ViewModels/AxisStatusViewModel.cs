using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EfemSimulation.ViewModels
{
    public class AxisStatusViewModel : INotifyPropertyChanged
    {
        private int _axisIndex;
        private bool _servoOn;
        private double _actualPosition;
        private double _velocity;
        private uint _alarmCode;
        private string _statusText;

        public int AxisIndex
        {
            get => _axisIndex;
            set => SetProperty(ref _axisIndex, value);
        }

        public bool ServoOn
        {
            get => _servoOn;
            set => SetProperty(ref _servoOn, value);
        }

        public double ActualPosition
        {
            get => _actualPosition;
            set => SetProperty(ref _actualPosition, value);
        }

        public double Velocity
        {
            get => _velocity;
            set => SetProperty(ref _velocity, value);
        }

        public uint AlarmCode
        {
            get => _alarmCode;
            set
            {
                if (SetProperty(ref _alarmCode, value))
                {
                    OnPropertyChanged(nameof(AlarmText));
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string AlarmText
        {
            get => _alarmCode == 0 ? "정상" : $"[알람] {_alarmCode}";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}