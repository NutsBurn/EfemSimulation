using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WMX3ApiCLR;
using EfemSimulation.Models;
using EfemSimulation.ViewModels;

namespace EfemSimulation
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<AxisStatusViewModel> _axisStatuses;

        public ObservableCollection<AxisStatusViewModel> AxisStatuses
        {
            get => _axisStatuses;
            set
            {
                if (_axisStatuses != value)
                {
                    _axisStatuses = value;
                    OnPropertyChanged(nameof(AxisStatuses));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class MainWindow : Window
    {
        private DispatcherTimer _simTimer;
        private AxisStatusManager _axisStatusManager;
        private DispatcherTimer _statusPollingTimer;

        // 실제 물리 공간 정의 (단위: mm)
        private const double StageMaxMM = 300.0; // 장비 최대 가동 범위 (300mm x 300mm)
        private const double CanvasSize = 300.0; // Canvas 픽셀 크기

        // 출발지, 웨이퍼 위치, 최종 목적지 설정 (mm 단위)
        private Point _startPositionMM = new Point(30, 30);      // 최초 시작점 (Z축만 이동)
        private Point _targetPositionMM = new Point(30, 250);    // 웨이퍼가 있는 장소 (Pick 위치)
        private Point _goalPositionMM = new Point(270, 40);       // 최종 목적지 (Place 위치)

        // Z축 관련 상수
        private const double Z_AXIS_UP = 50.0;      // Z축 상승 위치 (50mm)
        private const double Z_AXIS_DOWN = 0.0;     // Z축 하강 위치 (0mm)
        private const int Z_AXIS_INDEX = 2;         // Z축 인덱스

        // 현재 웨이퍼 위치 및 상태
        private Point _currentMM;
        private double _t = 0.0; // 시뮬레이션 진행 시간 파라미터 (0.0 ~ 1.0)
        private bool _waferGrasped = false; // 웨이퍼 그립 상태
        private bool _homingStarted = false; // 호밍 상태

        // ScottPlot 데이터 스트리머
        private ScottPlot.Plottables.DataStreamer _xStreamer;
        private ScottPlot.Plottables.DataStreamer _yStreamer;
        private ScottPlot.Plottables.DataStreamer _zStreamer;

        // 모달 윈도우 참조
        private SpeedGraphWindow _speedGraphWindow;

        private MainWindowViewModel _viewModel;

        // 펄스와 밀리미터 변환 관련 상수
        private const double PulsesPerStage = 100000.0; // 스테이지당 펄스 수

        public MainWindow()
        {
            InitializeComponent();
            InitSimulationEnvironment();
        }

        private void InitSimulationEnvironment()
        {
            // 1. 그래프 초기화
            InitializeSpeedGraph();

            // 2. 미니맵 요소 고정 배치 (mm -> px 변환 적용)
            // Start 위치
            Point startPx = ConvertMMToPx(_startPositionMM);
            Canvas.SetLeft(StartStation, startPx.X - 20);
            Canvas.SetTop(StartStation, startPx.Y - 20);
            Canvas.SetLeft(TxtStart, startPx.X - 20);
            Canvas.SetTop(TxtStart, startPx.Y - 20);

            // Target 위치 (웨이퍼 초기 위치)
            Point targetPx = ConvertMMToPx(_targetPositionMM);
            Canvas.SetLeft(TargetStation, targetPx.X - 20);
            Canvas.SetTop(TargetStation, targetPx.Y - 20);
            Canvas.SetLeft(TxtTarget, targetPx.X - 20);
            Canvas.SetTop(TxtTarget, targetPx.Y - 20);

            // Goal 위치 (최종 목적지)
            Point goalPx = ConvertMMToPx(_goalPositionMM);
            Canvas.SetLeft(GoalStation, goalPx.X - 20);
            Canvas.SetTop(GoalStation, goalPx.Y - 20);
            Canvas.SetLeft(TxtGoal, goalPx.X - 20);
            Canvas.SetTop(TxtGoal, goalPx.Y - 20);

            UpdateStagePositionOnCanvas(_startPositionMM);
            // 웨이퍼 초기 위치 (Target 위치에 배치)
            UpdateWaferPositionOnCanvas(_targetPositionMM);

            // 3. 타이머 생성
            _simTimer = new DispatcherTimer();
            _simTimer.Interval = TimeSpan.FromMilliseconds(20);
            _simTimer.Tick += SimulationLoop;

            AddLog("시뮬레이션 환경 초기화 완료");
        }

        private void InitializeSpeedGraph()
        {
            // 모달 윈도우가 열려있다면 닫기
            if (_speedGraphWindow != null && _speedGraphWindow.IsVisible)
            {
                _speedGraphWindow.Close();
            }

            // 새 모달 윈도우 생성
            _speedGraphWindow = new SpeedGraphWindow();

            // SpeedGraphWindow의 PlotControl에서 그래프 초기화
            var speedPlot = _speedGraphWindow.SpeedPlot;

            _xStreamer = speedPlot.Plot.Add.DataStreamer(500);
            _yStreamer = speedPlot.Plot.Add.DataStreamer(500);
            _zStreamer = speedPlot.Plot.Add.DataStreamer(500);
            _xStreamer.Color = ScottPlot.Color.FromHex("#FF5733");
            _yStreamer.Color = ScottPlot.Color.FromHex("#33FF57");
            _zStreamer.Color = ScottPlot.Color.FromHex("#3357FF");
            _xStreamer.Label = "X-Axis Motor Speed";
            _yStreamer.Label = "Y-Axis Motor Speed";
            _zStreamer.Label = "Z-Axis Motor Speed";

            speedPlot.Plot.ShowLegend(ScottPlot.Alignment.UpperRight);
            speedPlot.Plot.Axes.SetLimitsY(-10, 500);
            speedPlot.Refresh();
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            Thread wmx3LogicThread = new Thread(() => Wmx3Logic())
            {
                IsBackground = true
            };

            wmx3LogicThread.Start();
        }

        private void Wmx3Logic()
        {
            WMX3Api Wmx3Lib = new WMX3Api();
            EngineStatus EnStatus = new EngineStatus();
            CoreMotionStatus CmStatus = new CoreMotionStatus();
            CoreMotion Wmx3Lib_cm = new CoreMotion(Wmx3Lib);

            AddLog("웨이퍼 이송 시뮬레이션 시작");

            // AxisStatusManager 생성
            _axisStatusManager = new AxisStatusManager(Wmx3Lib, Wmx3Lib_cm);

            // ViewModel 생성 및 DataContext 설정
            Dispatcher.Invoke(() =>
            {
                _viewModel = new MainWindowViewModel 
                { 
                    AxisStatuses = _axisStatusManager.AxisStatuses 
                };
                this.DataContext = _viewModel;
                StartStatusPolling();
            });

            Motion.LinearIntplCommand linearIntplCommand = new Motion.LinearIntplCommand();

            AddLog("프로그램 시작");
            Thread.Sleep(1000);

            // 디바이스 생성
            Wmx3Lib.CreateDevice("C:\\Program Files\\SoftServo\\WMX3\\", DeviceType.DeviceTypeNormal, 0xFFFFFFFF);
            Wmx3Lib.SetDeviceName("InterpolationMotion");

            // 서보 통신 시작
            for (uint i = 0; i < 100; i++)
            {
                Wmx3Lib.StartCommunication(0xFFFFFFFF);
                Wmx3Lib.GetEngineStatus(ref EnStatus);

                if (EnStatus.State == EngineState.Communicating)
                {
                    AddLog("서보 통신 연결 완료");
                    break;
                }

                if (i == 0)
                {
                    StartStatusPolling();
                }
            }

            // 모든 축 서보 ON
            Wmx3Lib_cm.AxisControl.SetServoOn(0, true);
            Wmx3Lib_cm.AxisControl.SetServoOn(1, true);
            Wmx3Lib_cm.AxisControl.SetServoOn(2, true);

            AddLog("서보 ON 명령 전송");

            while (true)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);

                if (CmStatus.AxesStatus[0].ServoOn &&
                    CmStatus.AxesStatus[1].ServoOn &&
                    CmStatus.AxesStatus[2].ServoOn)
                {
                    AddLog("🟢 모든 축 서보 ON 완료");
                    break;
                }

                Thread.Sleep(100);
            }
            // 호밍 시작 위치로 X/Y축 이동
            AddLog($"호밍 시작 위치로 이동: ({_startPositionMM.X:F1}mm, {_startPositionMM.Y:F1}mm)");
            MoveXYAxes(Wmx3Lib_cm, CmStatus, _startPositionMM, 5000, "호밍 시작 위치 이동");
            _homingStarted = true;

            // 호밍
            for (int i = 0; i < 3; i++)
            {
                Config.HomeParam homeParam = new Config.HomeParam();
                Wmx3Lib_cm.Config.GetHomeParam(i, ref homeParam);
                homeParam.HomeType = Config.HomeType.CurrentPos;
                Wmx3Lib_cm.Config.SetHomeParam(i, homeParam);
                Wmx3Lib_cm.Home.StartHome(i);
            }

            AddLog("호밍(Homing) 시작");

            while (true)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);

                // 현재 위치 표시 (호밍 중 위치 업데이트)
                double posX_pulse = CmStatus.AxesStatus[0].PosCmd;
                double posY_pulse = CmStatus.AxesStatus[1].PosCmd;

                double posX_mm = PulseToMM(0, posX_pulse);
                double posY_mm = PulseToMM(1, posY_pulse);

                Point currentPos = new Point(posX_mm, posY_mm);

                Dispatcher.Invoke(() =>
                {
                    UpdateStagePositionOnCanvas(currentPos);

                    if (_waferGrasped)
                    {
                        UpdateWaferPositionOnCanvas(currentPos);
                    }
                });

                if (CmStatus.AxesStatus[0].HomeDone &&
                    CmStatus.AxesStatus[1].HomeDone &&
                    CmStatus.AxesStatus[2].HomeDone)
                {
                    AddLog("호밍(Homing) 완료");
                    break;
                }
                Thread.Sleep(100);
            }

            // Step1. Z축 상승 (0mm → 50mm, 2초 이내)
            AddLog("Step1. 시작점(Start)에서 Z축 서보모터 상승 시작: 0mm → 50mm");
            MoveZAxis(Wmx3Lib_cm, Z_AXIS_UP, "Z축 상승");

            // Step2. Start → Target으로 X/Y축 이동 (5초 이내)
            AddLog("Step2. 웨이퍼 위치(Target)로 X/Y축 이동 시작");
            MoveXYAxes(Wmx3Lib_cm, CmStatus, _targetPositionMM, 60000, "웨이퍼 적재소 도달");

            // Step3. Z축 하강 (50mm → 0mm, 2초 이내)
            AddLog("Step3. Target에서 웨이퍼 픽업을 위해 Z축 서보모터 하강 시작: 50mm → 0mm");
            MoveZAxis(Wmx3Lib_cm, Z_AXIS_DOWN, "Z축 하강");

            // Step4. 웨이퍼 그립 (1초)
            AddLog("Step4. 웨이퍼 그립 시작 - 1초간 유지");
            Thread.Sleep(1000);
            AddLog("웨이퍼 그립 완료");
            _waferGrasped = true;

            // Step5. Z축 상승 (0mm → 50mm, 2초 이내)
            AddLog("Step5. 웨이퍼 픽업 후 Z축 서보모터 상승 시작: 0mm → 50mm");
            MoveZAxis(Wmx3Lib_cm, Z_AXIS_UP, "Z축 상승");

            // Step6. Target → Goal로 X/Y축 이동 (7초 이내)
            AddLog("Step6. 최종 목적지(Goal)로 X/Y축 이동 시작");
            MoveXYAxes(Wmx3Lib_cm, CmStatus, _goalPositionMM, 100000, "Goal 도달");

            // ========== 모션 완료 후 처리 ==========
            Wmx3Lib_cm.Motion.Wait(0);
            Wmx3Lib_cm.Motion.Wait(1);
            Wmx3Lib_cm.Motion.Wait(2);

            // 서보 OFF
            Wmx3Lib_cm.AxisControl.SetServoOn(0, false);
            Wmx3Lib_cm.AxisControl.SetServoOn(1, false);
            Wmx3Lib_cm.AxisControl.SetServoOn(2, false);

            while (true)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);
                if (!CmStatus.AxesStatus[0].ServoOn &&
                    !CmStatus.AxesStatus[1].ServoOn &&
                    !CmStatus.AxesStatus[2].ServoOn)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            // 통신 종료
            Wmx3Lib.StopCommunication(0xFFFFFFFF);
            Wmx3Lib.CloseDevice();
            Wmx3Lib_cm.Dispose();
            Wmx3Lib.Dispose();

            AddLog("프로그램 종료 - 웨이퍼 이송 완료");

            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("웨이퍼 이송 완료!", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        /// <summary>
        /// Z축을 목표 위치로 이동하는 메서드
        /// </summary>
        private void MoveZAxis(CoreMotion Wmx3Lib_cm, double targetZ_mm, string stepName)
        {
            CoreMotionStatus CmStatus = new CoreMotionStatus();

            // Z축 펄스 변환
            int targetZ_pulse = (int)(targetZ_mm * PulsesPerStage / StageMaxMM);

            Motion.PosCommand zAxisCmd = new Motion.PosCommand
            {
                Axis = Z_AXIS_INDEX,
                Target = targetZ_pulse
            };


            zAxisCmd.Profile.Type = ProfileType.Trapezoidal;
            zAxisCmd.Profile.Velocity = 60000;   // Z축 속도 60,000 pulse/s = 약 180mm/s
            zAxisCmd.Profile.Acc = 200000;
            zAxisCmd.Profile.Dec = 200000;

            Wmx3Lib_cm.Motion.StartPos(zAxisCmd);
            AddLog($"  Z축 이동 중 → 목표: {targetZ_mm:F1}mm");

            while (true)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);

                double currentZ_pulse = CmStatus.AxesStatus[Z_AXIS_INDEX].PosCmd;
                double currentZ_mm = (currentZ_pulse / PulsesPerStage) * StageMaxMM;

                //AddLog($"  Z축 위치: {currentZ_mm:F2}mm");

                if (CmStatus.AxesStatus[Z_AXIS_INDEX].InPos)
                {
                    AddLog($"{stepName} 완료 (Z={currentZ_mm:F2}mm)");
                    return;
                }

                Thread.Sleep(50);
            }
        }

        /// X/Y축을 목표 위치로 이동하는 메서드
        private void MoveXYAxes(CoreMotion Wmx3Lib_cm, CoreMotionStatus CmStatus, Point targetPos_mm, int velocityPps, string stepName)
        {
            // 목표 위치를 펄스로 변환
            int targetX_pulse = MMToPulse(0, targetPos_mm.X);
            int targetY_pulse = MMToPulse(1, targetPos_mm.Y);

            // 선형 보간 명령
            Motion.LinearIntplCommand xyCmd = new Motion.LinearIntplCommand();
            xyCmd.AxisCount = 2;
            xyCmd.Axis[0] = 0;  // X축
            xyCmd.Axis[1] = 1;  // Y축
            xyCmd.Target[0] = targetX_pulse;
            xyCmd.Target[1] = targetY_pulse;
            xyCmd.Profile.Type = ProfileType.Trapezoidal;
            xyCmd.Profile.Velocity = velocityPps;
            xyCmd.Profile.Acc = 200000;
            xyCmd.Profile.Dec = 200000;

            Wmx3Lib_cm.Motion.StartLinearIntplPos(xyCmd);
            AddLog($"  X/Y축 이동 중 → 목표: ({targetPos_mm.X:F1}mm, {targetPos_mm.Y:F1}mm)  속도: {velocityPps} pulse/s");

            int noUpdateCount = 0;

            while (true)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);

                double currentX_pulse = CmStatus.AxesStatus[0].PosCmd;
                double currentY_pulse = CmStatus.AxesStatus[1].PosCmd;
                double currentX_mm = PulseToMM(0, currentX_pulse);
                double currentY_mm = PulseToMM(1, currentY_pulse);

                Point currentPos = new Point(currentX_mm, currentY_mm);

                Dispatcher.Invoke(() =>
                {
                    UpdateStagePositionOnCanvas(currentPos);

                    if (_waferGrasped)
                    {
                        UpdateWaferPositionOnCanvas(currentPos);
                    }
                });

                // 목표 위치 도달 확인
                double errorX = Math.Abs(currentX_mm - targetPos_mm.X);
                double errorY = Math.Abs(currentY_mm - targetPos_mm.Y);
                double toleranceMM = 1.0;

                if (CmStatus.AxesStatus[0].InPos && CmStatus.AxesStatus[1].InPos && errorX <= toleranceMM && errorY <= toleranceMM)
                {
                    noUpdateCount++;
                    if (noUpdateCount >= 3)
                    {
                        AddLog($"{stepName} 완료 (X={currentX_mm:F2}mm, Y={currentY_mm:F2}mm)");
                        return;
                    }
                }
                else
                {
                    noUpdateCount = 0;
                }

                Thread.Sleep(50);
            }
        }

        private void SimulationLoop(object sender, EventArgs e)
        {
            _t += 0.01;
            if (_t > 1.0)
            {
                _t = 1.0;
                _simTimer.Stop();
                MessageBox.Show("웨이퍼 이송 완료!", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                AddLog("웨이퍼 이송 완료");
            }

            double smoothT = _t * _t * (3 - 2 * _t);

            Point prevMM = _currentMM;

            _currentMM.X = _startPositionMM.X + (_targetPositionMM.X - _startPositionMM.X) * smoothT;
            _currentMM.Y = _startPositionMM.Y + (_targetPositionMM.Y - _startPositionMM.Y) * smoothT;

            double xSpeed = Math.Abs(_currentMM.X - prevMM.X) / 0.02 * 10;
            double ySpeed = Math.Abs(_currentMM.Y - prevMM.Y) / 0.02 * 10;

            if (_speedGraphWindow != null && _speedGraphWindow.IsVisible)
            {
                _xStreamer.Add(xSpeed);
                _yStreamer.Add(ySpeed);
                _speedGraphWindow.SpeedPlot.Refresh();
            }

            UpdateWaferPositionOnCanvas(_currentMM);
        }

        private void UpdateStagePositionOnCanvas(Point positionMM)
        {
            Point pxPos = ConvertMMToPx(positionMM);

            Canvas.SetLeft(StagePosition, pxPos.X - 7);
            Canvas.SetTop(StagePosition, pxPos.Y - 7);

            if (_homingStarted)
            {
                TrajectoryLine.Points.Add(pxPos);
            }
        }

        private void UpdateWaferPositionOnCanvas(Point positionMM)
        {
            Point pxPos = ConvertMMToPx(positionMM);

            Canvas.SetLeft(Wafer, pxPos.X - 15);
            Canvas.SetTop(Wafer, pxPos.Y - 15);

            TrajectoryLine.Points.Add(pxPos);
        }

        private Point ConvertMMToPx(Point mmPoint)
        {
            double pxX = (mmPoint.X / StageMaxMM) * CanvasSize;
            double pxY = CanvasSize - ((mmPoint.Y / StageMaxMM) * CanvasSize);
            
            // 범위 체크 및 조정
            pxX = Math.Max(0, Math.Min(CanvasSize, pxX));
            pxY = Math.Max(0, Math.Min(CanvasSize, pxY));

            return new Point(pxX, pxY);
        }

        public void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var logMessage = $"[{timestamp}] {message}";
                LogListBox.Items.Insert(0, logMessage);
            
                if (LogListBox.Items.Count > 1000)
                {
                    LogListBox.Items.RemoveAt(LogListBox.Items.Count - 1);
                }
            });
        }

        // 속도 그래프 모달 윈도우 열기
        private void BtnShowSpeedGraph_Click(object sender, RoutedEventArgs e)
        {

            if (_speedGraphWindow == null || !_speedGraphWindow.IsVisible)
            {
                InitializeSpeedGraph();
                _speedGraphWindow.Owner = this;
                _speedGraphWindow.Show();
                AddLog("속도 그래프 윈도우 열기");
            }
            else
            {
                _speedGraphWindow.Focus();
                AddLog("속도 그래프 윈도우 활성화");
            }
        }

        // 서보 상태 폴링 시작 (100ms 주기)
        private void StartStatusPolling()
        {
            if (_statusPollingTimer != null)
                return;

            _statusPollingTimer = new DispatcherTimer();
            _statusPollingTimer.Interval = TimeSpan.FromMilliseconds(100);
            _statusPollingTimer.Tick += (s, e) =>
            {
                if (_axisStatusManager != null)
                {
                    _axisStatusManager.UpdateAxisStatus();
                }
            };
            _statusPollingTimer.Start();
            AddLog("모터 상태 폴링 시작 (100ms 주기)");
        }

        // Window 종료 시 타이머 정리
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_statusPollingTimer != null)
            {
                _statusPollingTimer.Stop();
                _statusPollingTimer = null;
            }

            if (_simTimer != null)
            {
                _simTimer.Stop();
                _simTimer = null;
            }
        }

        private double PulseToMM(int axisIndex, double pulse)
        {
            double relativeMM = (pulse / PulsesPerStage) * StageMaxMM;

            if (axisIndex == 0)
            {
                return _startPositionMM.X + relativeMM;
            }

            if (axisIndex == 1)
            {
                return _startPositionMM.Y + relativeMM;
            }

            return relativeMM;
        }

        private int MMToPulse(int axisIndex, double positionMM)
        {
            double relativeMM = positionMM;

            if (axisIndex == 0)
            {
                relativeMM -= _startPositionMM.X;
            }
            else if (axisIndex == 1)
            {
                relativeMM -= _startPositionMM.Y;
            }

            return (int)(relativeMM * PulsesPerStage / StageMaxMM);
        }
    }
}
