using System;
using System.Collections.ObjectModel;
using WMX3ApiCLR;
using EfemSimulation.ViewModels;

namespace EfemSimulation.Models
{
    public class AxisStatusManager
    {
        private readonly WMX3Api _wmx3Lib;
        private readonly CoreMotion _coreMotion;
        public ObservableCollection<AxisStatusViewModel> AxisStatuses { get; }

        public AxisStatusManager(WMX3Api wmx3Lib, CoreMotion coreMotion)
        {
            _wmx3Lib = wmx3Lib ?? throw new ArgumentNullException(nameof(wmx3Lib));
            _coreMotion = coreMotion ?? throw new ArgumentNullException(nameof(coreMotion));

            AxisStatuses = new ObservableCollection<AxisStatusViewModel>
            {
                new AxisStatusViewModel { AxisIndex = 0 },
                new AxisStatusViewModel { AxisIndex = 1 },
                new AxisStatusViewModel { AxisIndex = 2 }
            };
        }

        /// <summary>
        /// 모든 축의 상태를 폴링하여 ViewModel 업데이트
        /// </summary>
        public bool UpdateAxisStatus()
        {
            try
            {
                CoreMotionStatus cmStatus = new CoreMotionStatus();
                _coreMotion.GetStatus(ref cmStatus);

                for (int i = 0; i < 3; i++)
                {
                    var axisStatus = cmStatus.AxesStatus[i];
                    var viewModel = AxisStatuses[i];

                    // Servo On/Off 상태
                    viewModel.ServoOn = axisStatus.ServoOn;
                    viewModel.StatusText = axisStatus.ServoOn ? "🟢 Servo ON" : "🔴 Servo OFF";

                    // 현재 위치 (pulse -> mm 변환)
                    double positionMM = (axisStatus.PosCmd / 100000.0) * 300.0;
                    viewModel.ActualPosition = positionMM;

                    // 속도 (pulse/sec -> mm/sec 변환)
                    double velocityMMPerSec = (axisStatus.VelocityCmd / 100000.0) * 300.0;
                    viewModel.Velocity = velocityMMPerSec;

                    // 알람 코드
                    viewModel.AlarmCode = (uint)axisStatus.AmpAlarmCode;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AxisStatusManager 업데이트 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 모든 축이 Servo ON 상태인지 확인
        /// </summary>
        public bool AreAllServosOn()
        {
            foreach (var status in AxisStatuses)
            {
                if (!status.ServoOn)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 알람이 발생한 축이 있는지 확인
        /// </summary>
        public bool HasAnyAlarm()
        {
            foreach (var status in AxisStatuses)
            {
                if (status.AlarmCode != 0)
                    return true;
            }
            return false;
        }
    }
}