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

        // 출발지 및 목적지 설정 (mm 단위)
        private Point _startMM = new Point(30, 250);   // 좌측 하단 부근
        private Point _targetMM = new Point(270, 40);  // 우측 상단 부근

        // 현재 웨이퍼 위치 및 상태
        private Point _currentMM;
        private double _t = 0.0; // 시뮬레이션 진행 시간 파라미터 (0.0 ~ 1.0)

        // ScottPlot 데이터 스트리머
        private ScottPlot.Plottables.DataStreamer _xStreamer;
        private ScottPlot.Plottables.DataStreamer _yStreamer;

        // 모달 윈도우 참조
        private SpeedGraphWindow _speedGraphWindow;

        private MainWindowViewModel _viewModel;

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
            Point startPx = ConvertMMToPx(_startMM);
            Canvas.SetLeft(StartStation, startPx.X - 20); // 중심 맞추기
            Canvas.SetTop(StartStation, startPx.Y - 20);
            Canvas.SetLeft(TxtStart, startPx.X - 15);
            Canvas.SetTop(TxtStart, startPx.Y - 8);

            Point targetPx = ConvertMMToPx(_targetMM);
            Canvas.SetLeft(TargetStation, targetPx.X - 20);
            Canvas.SetTop(TargetStation, targetPx.Y - 20);
            Canvas.SetLeft(TxtTarget, targetPx.X - 20);
            Canvas.SetTop(TxtTarget, targetPx.Y - 8);

            // 초기 위치 설정
            _currentMM = _startMM;
            UpdateWaferPositionOnCanvas(_currentMM);

            // 3. 타이머 생성 (20ms 주기 = 50Hz 장비 샘플링 모사)
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
            _xStreamer.Color = ScottPlot.Color.FromHex("#FF5733");
            _yStreamer.Color = ScottPlot.Color.FromHex("#33FF57");
            _xStreamer.Label = "X-Axis Motor Speed";
            _yStreamer.Label = "Y-Axis Motor Speed";

            speedPlot.Plot.ShowLegend(ScottPlot.Alignment.UpperRight);
            speedPlot.Plot.Axes.SetLimitsY(-10, 500);
            speedPlot.Refresh();
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            //_t = 0.0;
            //_currentMM = _startMM;
            //TrajectoryLine.Points.Clear();
            //_simTimer.Start();

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

            AddLog("이송 시뮬레이션 시작");

            // AxisStatusManager 생성
            _axisStatusManager = new AxisStatusManager(Wmx3Lib, Wmx3Lib_cm);

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
            Motion.CenterAndLengthCircularIntplCommand circularIntplCommand = new Motion.CenterAndLengthCircularIntplCommand();
            Motion.HelicalIntplCommand helicalCommand = new Motion.HelicalIntplCommand();

            AddLog("프로그램 시작");
            Thread.Sleep(1000);

            // 일반 디바이스 생성
            Wmx3Lib.CreateDevice("C:\\Program Files\\SoftServo\\WMX3\\", DeviceType.DeviceTypeNormal, 0xFFFFFFFF);

            // 디바이스 이름 설정
            Wmx3Lib.SetDeviceName("InterpolationMotion");

            // 서보 네트워크 통신시작
            for (uint i = 0; i < 100; i++)
            {
                Wmx3Lib.StartCommunication(0xFFFFFFFF);
                Wmx3Lib.GetEngineStatus(ref EnStatus);

                if (EnStatus.State == EngineState.Communicating)
                {
                    AddLog("서보 통신 연결 완료");
                    break;
                }

                // 폴링 시작 (초기 단계에서도 상태 표시)
                if (i == 0)
                {
                    StartStatusPolling();
                }
            }

            // 서보 ON
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
                double posX_mm = (posX_pulse / 100000.0) * 300.0;
                double posY_mm = (posY_pulse / 100000.0) * 300.0;
                Point currentPos = new Point(posX_mm, posY_mm);

                Dispatcher.Invoke(() =>
                {
                    UpdateWaferPositionOnCanvas(currentPos);
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


            // 시작 지점으로 이동 (상대 위치 이동으로 변경)
            Motion.LinearIntplCommand moveToStartCommand = new Motion.LinearIntplCommand();
            moveToStartCommand.AxisCount = 2;
            moveToStartCommand.Axis[0] = 0;
            moveToStartCommand.Axis[1] = 1;
            moveToStartCommand.Target[0] = (int)(_startMM.X * 100000.0 / 300.0);  // 30mm → pulse 변환
            moveToStartCommand.Target[1] = (int)(_startMM.Y * 100000.0 / 300.0);  // 250mm → pulse 변환

            moveToStartCommand.Profile.Type = ProfileType.Trapezoidal;
            moveToStartCommand.Profile.Velocity = 50000;
            moveToStartCommand.Profile.Acc = 150000;
            moveToStartCommand.Profile.Dec = 150000;

            Wmx3Lib_cm.Motion.StartLinearIntplPos(moveToStartCommand);
            AddLog($"시작점({_startMM.X:F1}, {_startMM.Y:F1})으로 이동 중...");


            // 알람 체크: 모션 시작 전 알람 확인
            if (_axisStatusManager.HasAnyAlarm())
            {
                AddLog("⚠️ 알람 발생! 모션 실행 차단");
                return;
            }

            // 시작 지점 도달 대기
            bool isMovingToStart = true;
            int startNoUpdateCount = 0;
            int startLogInterval = 0;

            while (isMovingToStart)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);

                // 현재 위치 표시
                double posX_pulse = CmStatus.AxesStatus[0].PosCmd;
                double posY_pulse = CmStatus.AxesStatus[1].PosCmd;

                double posX_mm = (posX_pulse / 100000.0) * 300.0;
                double posY_mm = (posY_pulse / 100000.0) * 300.0;

                Point currentPos = new Point(posX_mm, posY_mm);

                startLogInterval++;
                if (startLogInterval >= 10)  // 50ms * 10 = 500ms 주기
                {
                    AddLog($"시작점 이동 중 - 현재 위치: X={posX_mm:F2} mm, Y={posY_mm:F2} mm | InPos: X={CmStatus.AxesStatus[0].InPos}, Y={CmStatus.AxesStatus[1].InPos}");
                    startLogInterval = 0;
                }

                Dispatcher.Invoke(() =>
                {
                    UpdateWaferPositionOnCanvas(currentPos);
                });

                double targetStartX_mm = _startMM.X;
                double targetStartY_mm = _startMM.Y;
                double errorStartX = Math.Abs(posX_mm - targetStartX_mm);
                double errorStartY = Math.Abs(posY_mm - targetStartY_mm);
                double toleranceMM = 5.0;  // ±5mm 허용

                if (CmStatus.AxesStatus[0].InPos && CmStatus.AxesStatus[1].InPos &&
                    errorStartX <= toleranceMM && errorStartY <= toleranceMM)
                {
                    startNoUpdateCount++;
                    AddLog($"시작점 근처 도달 감지 - 카운트: {startNoUpdateCount}/3");

                    if (startNoUpdateCount >= 3)  // 3회 연속 확인
                    {
                        isMovingToStart = false;
                    }
                }
                else
                {
                    if (startNoUpdateCount > 0)
                    {
                        AddLog($"시작점 도달 카운트 초기화 (거리 오차: X={errorStartX:F2}mm, Y={errorStartY:F2}mm)");
                    }
                    startNoUpdateCount = 0;  // 초기화
                }
                System.Threading.Thread.Sleep(50);
            }

            double finalStartX_pulse = CmStatus.AxesStatus[0].PosCmd;
            double finalStartY_pulse = CmStatus.AxesStatus[1].PosCmd;
            double finalStartX_mm = (finalStartX_pulse / 100000.0) * 300.0;
            double finalStartY_mm = (finalStartY_pulse / 100000.0) * 300.0;
            AddLog($"시작 지점 도달 - 최종 위치: X={finalStartX_mm:F2} mm, Y={finalStartY_mm:F2} mm");
            Thread.Sleep(500);  // 모션 안정화 대기


            // Next Step. 목적지로 이동
            linearIntplCommand.AxisCount = 2;
            linearIntplCommand.Axis[0] = 0;
            linearIntplCommand.Axis[1] = 1;
            linearIntplCommand.Target[0] = (int)(_targetMM.X * 100000.0 / 300.0);  // 270mm → pulse 변환
            linearIntplCommand.Target[1] = (int)(_targetMM.Y * 100000.0 / 300.0);  // 40mm → pulse 변환

            linearIntplCommand.Profile.Type = ProfileType.Trapezoidal;
            linearIntplCommand.Profile.Velocity = 10000;
            linearIntplCommand.Profile.Acc = 50000;
            linearIntplCommand.Profile.Dec = 50000;

            Wmx3Lib_cm.Motion.StartLinearIntplPos(linearIntplCommand);
            //AddLog($"목적지로 이동 시작 - Target: ({linearIntplCommand.Target[0]}, {linearIntplCommand.Target[1]}) Pulse");
            AddLog($"목적지({_targetMM.X:F1}, {_targetMM.Y:F1})으로 이동 중...");


            // 모션 진행 중 실시간 위치 업데이트
            bool isMoving = true;
            int noUpdateCount = 0;  // 위치 변화 없는 횟수 추적
            int logInterval = 0;

            while (isMoving)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);

                // 축 0(X축), 축 1(Y축)의 현재 위치 획득 (단위: pulse)
                double posX_pulse = CmStatus.AxesStatus[0].PosCmd;
                double posY_pulse = CmStatus.AxesStatus[1].PosCmd;

                // Pulse를 mm로 변환 (0~100000 pulse → 0~300mm 범위)
                double posX_mm = (posX_pulse / 100000.0) * 300.0;
                double posY_mm = (posY_pulse / 100000.0) * 300.0;

                // 범위 제한
                posX_mm = Math.Min(Math.Max(posX_mm, 0), StageMaxMM);
                posY_mm = Math.Min(Math.Max(posY_mm, 0), StageMaxMM);

                Point currentPos = new Point(posX_mm, posY_mm);


                logInterval++;
                if (logInterval >= 10)  // 50ms * 10 = 500ms 주기
                {
                    AddLog($"목적지 이동 중 - 현재 위치: X={posX_mm:F2} mm, Y={posY_mm:F2} mm | InPos: X={CmStatus.AxesStatus[0].InPos}, Y={CmStatus.AxesStatus[1].InPos}");
                    logInterval = 0;
                }
                
                // UI 업데이트 (Dispatcher 사용)
                Dispatcher.Invoke(() =>
                {
                    UpdateWaferPositionOnCanvas(currentPos);

                    // 그래프 업데이트
                    if (_speedGraphWindow != null && _speedGraphWindow.IsVisible)
                    {
                        _speedGraphWindow.SpeedPlot.Refresh();
                    }
                });

                //InPos 상태 변화 감지를 위한 추가 검증
                // 목표 위치와의 거리 확인 (허용 오차: ±5mm)
                double targetX_mm = _targetMM.X;
                double targetY_mm = _targetMM.Y;
                double errorX = Math.Abs(posX_mm - targetX_mm);
                double errorY = Math.Abs(posY_mm - targetY_mm);
                double toleranceMM = 5.0;  // ±5mm 허용

                // 모션 완료 확인 (모든 축의 이동이 완료되었는지 확인)
                if (CmStatus.AxesStatus[0].InPos && CmStatus.AxesStatus[1].InPos &&
                    errorX <= toleranceMM && errorY <= toleranceMM)
                {
                    noUpdateCount++;
                    AddLog($"목적지 근처 도달 감지 - 카운트: {noUpdateCount}/3");

                    if (noUpdateCount >= 3)  // 3회 연속 InPos 확인 후 이동 완료
                    {
                        isMoving = false;
                        AddLog($"목적지 도달 완료 - 최종 위치: X={posX_mm:F2} mm, Y={posY_mm:F2} mm");
                    }
                }else
                {
                    if (noUpdateCount > 0)
                    {
                        AddLog($"목적지 도달 카운트 초기화 (거리 오차: X={errorX:F2}mm, Y={errorY:F2}mm)");
                    }
                    noUpdateCount = 0;  // 초기화
                }

                System.Threading.Thread.Sleep(50);  // 50ms 주기로 위치 업데이트
            }

            double finalEndX_pulse = CmStatus.AxesStatus[0].PosCmd;
            double finalEndY_pulse = CmStatus.AxesStatus[1].PosCmd;
            double finalEndX_mm = (finalEndX_pulse / 100000.0) * 300.0;
            double finalEndY_mm = (finalEndY_pulse / 100000.0) * 300.0;
            AddLog($"목적지 도달 완료 - 최종 위치: X={finalEndX_mm:F2} mm, Y={finalEndY_mm:F2} mm");
            AddLog("선형 보간 이동 완료");

            Wmx3Lib_cm.Motion.Wait(0);
            Wmx3Lib_cm.Motion.Wait(1);
            Wmx3Lib_cm.Motion.Wait(2);


            // Set servo off.
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

                System.Threading.Thread.Sleep(100);
            }

            // Stop Communication.
            Wmx3Lib.StopCommunication(0xFFFFFFFF);

            //Quit device.
            Wmx3Lib.CloseDevice();
            Wmx3Lib_cm.Dispose();
            Wmx3Lib.Dispose();

            AddLog("프로그램 종료");

            // 최종 위치 업데이트
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("웨이퍼 이송 완료!", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            });

            Thread.Sleep(3000);
        }

        private void SimulationLoop(object sender, EventArgs e)
        {
            _t += 0.01; // 이동 속도 제어 변수
            if (_t > 1.0)
            {
                _t = 1.0;
                _simTimer.Stop();
                MessageBox.Show("웨이퍼 이송 완료!", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                AddLog("웨이퍼 이송 완료");
            }

            // 부드러운 가감속(Smooth Step / S-Curve 모사)을 위한 보간 수식
            // Smoothstep: 3t^2 - 2t^3
            double smoothT = _t * _t * (3 - 2 * _t);

            // 이전 위치 저장 (속도 계산용)
            Point prevMM = _currentMM;

            // 현재 mm 위치 계산 (선형 보간)
            _currentMM.X = _startMM.X + (_targetMM.X - _startMM.X) * smoothT;
            _currentMM.Y = _startMM.Y + (_targetMM.Y - _startMM.Y) * smoothT;

            // 모터 변속량(속도 = 변위 차이 / 시간) 계산 및 스케일링
            double xSpeed = Math.Abs(_currentMM.X - prevMM.X) / 0.02 * 10;
            double ySpeed = Math.Abs(_currentMM.Y - prevMM.Y) / 0.02 * 10;

            // 그래프 업데이트 (모달 윈도우가 열려있을 때만)
            if (_speedGraphWindow != null && _speedGraphWindow.IsVisible)
            {
                _xStreamer.Add(xSpeed);
                _yStreamer.Add(ySpeed);
                _speedGraphWindow.SpeedPlot.Refresh();
            }

            // 2D 미니맵 웨이퍼 및 궤적 업데이트
            UpdateWaferPositionOnCanvas(_currentMM);
        }

        private void UpdateWaferPositionOnCanvas(Point positionMM)
        {
            Point pxPos = ConvertMMToPx(positionMM);

            // 웨이퍼 위치 이동 (Wafer 반지름인 15 차감하여 중심 정렬)
            Canvas.SetLeft(Wafer, pxPos.X - 15);
            Canvas.SetTop(Wafer, pxPos.Y - 15);

            // 궤적 라인에 포인트 추가
            TrajectoryLine.Points.Add(pxPos);
        }

        // 핵심 수식: 실제 장비 mm 좌표를 UI Canvas Pixel 좌표로 변환
        private Point ConvertMMToPx(Point mmPoint)
        {
            double pxX = (mmPoint.X / StageMaxMM) * CanvasSize;
            // WPF Canvas는 하단으로 갈수록 Y가 증가하므로, 장비 좌표계와 맞추기 위해 Y축을 뒤집어줍니다.
            double pxY = CanvasSize - ((mmPoint.Y / StageMaxMM) * CanvasSize);

            return new Point(pxX, pxY);
        }

        // 로그 추가 메서드
        public void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {

                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var logMessage = $"[{timestamp}] {message}";
                LogListBox.Items.Insert(0, logMessage);
            
                // 최대 1000개 로그만 유지
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
    }
}
