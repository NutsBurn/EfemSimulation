using System;
using System.Collections.ObjectModel;
using System.Threading;
using WMX3ApiCLR;
using EfemSimulation.ViewModels;

namespace EfemSimulation.Models
{
      // 백그라운드 스레드가 만든 불변 스냅샷 (UI 접근 없음)
    public sealed class AxisSnapshot
    {
        public bool ServoOn;
        public double PositionMM;
        public double VelocityMM;
        public uint AlarmCode;
    }

    public class AxisStatusManager
    {
        private readonly WMX3Api _wmx3Lib;
        private readonly CoreMotion _coreMotion;
        public ObservableCollection<AxisStatusViewModel> AxisStatuses { get; }

        private AxisSnapshot[] _latest = new AxisSnapshot[0];
        private Thread _snapshotThread;
        private volatile bool _snapshotRunning;

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

            // 스냅샷 생성 시작
        public void StartSnapshot(int intervalMs = 50)
        {
            if (_snapshotRunning)
                return;

            _snapshotRunning = true;
            _snapshotThread = new Thread(() =>
            {
                while (_snapshotRunning)
                {
                    CaptureSnapshot();
                    Thread.Sleep(intervalMs);
                }
            })
            {
                IsBackground = true,
                Name = "AxisStatusSnapshot"
            };
            _snapshotThread.Start();
        }

            // 스냅샷 생성 중지
        public void StopSnapshot()
        {
            _snapshotRunning = false;
            _snapshotThread?.Join(300);
        }

            // WMX3 상태 스냅샷 생성
        private bool CaptureSnapshot()
        {
            try
            {
                CoreMotionStatus cmStatus = new CoreMotionStatus();
                _coreMotion.GetStatus(ref cmStatus);

                var snap = new AxisSnapshot[3];
                for (int i = 0; i < 3; i++)
                {
                    var axisStatus = cmStatus.AxesStatus[i];
                    snap[i] = new AxisSnapshot
                    {
                        ServoOn = axisStatus.ServoOn,
                        PositionMM = (axisStatus.PosCmd / 100000.0) * 300.0,
                        VelocityMM = (axisStatus.VelocityCmd / 100000.0) * 300.0,
                        AlarmCode = (uint)axisStatus.AmpAlarmCode
                    };
                }

                Volatile.Write(ref _latest, snap);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"스냅샷 생성 실패: {ex.Message}");
                return false;
            }
        }

            // UI 스레드: 최신 스냅샷을 ViewModel에 반영
        public bool UpdateAxisStatus()
        {
            var snapShot = Volatile.Read(ref _latest);
            if (snapShot.Length == 0)
                return false;

            for (int i = 0; i < snapShot.Length; i++)
            {
                var viewModel = AxisStatuses[i];

                viewModel.ServoOn = snapShot[i].ServoOn;
                viewModel.StatusText = snapShot[i].ServoOn ? "🟢 Servo ON" : "🔴 Servo OFF";
                viewModel.ActualPosition = snapShot[i].PositionMM;
                viewModel.Velocity = snapShot[i].VelocityMM;
                viewModel.AlarmCode = snapShot[i].AlarmCode;
            }

            return true;
        }

            // 모든 축이 Servo ON 상태인지 확인
        public bool AreAllServosOn()
        {
            foreach (var status in AxisStatuses)
            {
                if (!status.ServoOn)
                    return false;
            }
            return true;
        }

            // 알람이 발생한 축이 있는지 확인
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