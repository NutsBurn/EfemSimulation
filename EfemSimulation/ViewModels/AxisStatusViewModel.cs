using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace EfemSimulation.ViewModels
{
    public class AxisStatusViewModel : INotifyPropertyChanged
    {
        private int _axisIndex;
        private bool _servoOn;
        private double _actualPosition;
        private double _velocity;
        private uint _alarmCode;
        private string _statusText = "🔴 미연결";
        private SolidColorBrush _statusColor = new SolidColorBrush(Color.FromRgb(255, 68, 68)); // 빨간색 (#FF4444)

        // 미리 생성된 색상 브러시 (UI 스레드에서만 한 번 생성)
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(255, 68, 68));    // 🔴 미연결
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(34, 255, 85));   // 🟢 Servo ON
        private static readonly SolidColorBrush YellowBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));  // ⚠️ 경보

        public event PropertyChangedEventHandler PropertyChanged;

        public int AxisIndex
        {
            get => _axisIndex;
            set => SetProperty(ref _axisIndex, value);
        }

        public bool ServoOn
        {
            get => _servoOn;
            set
            {
                if (SetProperty(ref _servoOn, value))
                {
                    UpdateStatusDisplay();
                }
            }
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
                    UpdateStatusDisplay();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public SolidColorBrush StatusColor
        {
            get => _statusColor;
            set => SetProperty(ref _statusColor, value);
        }

        public string AlarmText
        {
            get => _alarmCode == 0 ? "정상" : $"[경보] {_alarmCode}";
        }

        public SolidColorBrush AlarmTextColor
        {
            get => _alarmCode == 0 
                ? new SolidColorBrush(Color.FromRgb(34, 255, 85))    // 초록색
                : new SolidColorBrush(Color.FromRgb(255, 68, 68));   // 빨간색
        }

        // 상태에 따라 텍스트와 색상 업데이트
        private void UpdateStatusDisplay()
        {
            if (_servoOn)
            {
                // 서보 ON 상태
                if (_alarmCode != 0)
                {
                    // 알람 발생
                    StatusText = $"⚠️ 경보 (Alarm: {_alarmCode})";
                    StatusColor = YellowBrush;
                }
                else
                {
                    // 정상 작동
                    StatusText = "🟢 Servo ON";
                    StatusColor = GreenBrush;
                }
            }
            else
            {
                // 서보 OFF 상태 (미연결)
                StatusText = "🔴 미연결";
                StatusColor = RedBrush;
            }
        }

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