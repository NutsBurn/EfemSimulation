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
        private DispatcherTimer _logFlushTimer;

            // WMX3 라이브러리 인스턴스를 클래스 멤버로 선언
        private WMX3Api Wmx3Lib;
        private EngineStatus EnStatus;
        private CoreMotionStatus CmStatus;
        private CoreMotion _wmx3Lib_cm;

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

        private MainWindowViewModel _viewModel;

            // 펄스와 밀리미터 변환 관련 상수
        private const double PulsesPerStage = 100000.0; // 스테이지당 펄스 수

            // 상태 머신 및 스레드 관련 변수
        private MotionStateMachine _stateMachine;
        private Thread _wmx3LogicThread;
        private bool _motionThreadRunning = false;

            // 긴급 정지 감속도 (pulse/s²)
        private const double EMERGENCY_DECELERATION = 1000000.0;

            // WMX3 디바이스 생성 여부
        private bool _deviceCreated = false;

            // 인터록 복구 관련 상태
        private bool _interlockPending = false;     // 긴급정지 후 복구 대기 중
        private bool _interlockRecovering = false;  // 복구 동작 수행 중

        public MainWindow()
        {
            Wmx3Lib = new WMX3Api();
            EnStatus = new EngineStatus();
            CmStatus = new CoreMotionStatus();
            _wmx3Lib_cm = new CoreMotion(Wmx3Lib);

            InitializeComponent();
            _stateMachine = new MotionStateMachine();
            InitializeViewModel();
            InitSimulationEnvironment();
        }

        private void InitializeViewModel()
        {
                  // 초기 ViewModel 생성 (WMX3 없이 기본 상태만 표시)
            _viewModel = new MainWindowViewModel
            {
                AxisStatuses = new ObservableCollection<AxisStatusViewModel>
                {
                    new AxisStatusViewModel
                    {
                        AxisIndex = 0,
                        StatusText = "🔴 미연결",
                        StatusColor = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(255, 68, 68)),
                        ActualPosition = 0.0,
                        Velocity = 0.0,
                        AlarmCode = 0,
                        ServoOn = false
                    },
                    new AxisStatusViewModel
                    {
                        AxisIndex = 1,
                        StatusText = "🔴 미연결",
                        StatusColor = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(255, 68, 68)),
                        ActualPosition = 0.0,
                        Velocity = 0.0,
                        AlarmCode = 0,
                        ServoOn = false
                    },
                    new AxisStatusViewModel
                    {
                        AxisIndex = 2,
                        StatusText = "🔴 미연결",
                        StatusColor = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(255, 68, 68)),
                        ActualPosition = 0.0,
                        Velocity = 0.0,
                        AlarmCode = 0,
                        ServoOn = false
                    }
                }
            };

            this.DataContext = _viewModel;
            AddLog("✓ UI 초기화 완료");
        }

        private void InitSimulationEnvironment()
        {
                  // 1. 미니맵 요소 고정 배치 (mm -> px 변환 적용)
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

                   // 2. 타이머 생성
            _simTimer = new DispatcherTimer();
            _simTimer.Interval = TimeSpan.FromMilliseconds(20);
            _simTimer.Tick += SimulationLoop;

            AddLog("시뮬레이션 환경 초기화 완료");
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_stateMachine.CurrentState == MotionState.Idle)
            {
                _stateMachine.RequestStart();
                
                _wmx3LogicThread = new Thread(() => Wmx3MotionLoop())
                {
                    IsBackground = true
                };
                _motionThreadRunning = true;
                _wmx3LogicThread.Start();

                AddLog("▶ 웨이퍼 이송 시뮬레이션 시작");
            }
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_stateMachine.CurrentState == MotionState.Running)
            {
                _stateMachine.RequestPause();
                AddLog("⏸ 일시정지 요청됨");
            }
        }

        private void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            if (_stateMachine.CurrentState == MotionState.Paused)
            {
                _stateMachine.RequestResume();
                AddLog("▶ 재개 요청됨");
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_stateMachine.CurrentState == MotionState.Running ||
                _stateMachine.CurrentState == MotionState.Paused)
            {
                _stateMachine.RequestStop();
                AddLog("⏹ 정지 요청됨 - 현재 사이클 완료 후 원점복귀");
            }
        }

        private void BtnInterlock_Click(object sender, RoutedEventArgs e)
        {
            _stateMachine.RequestInterlock();
            ImmediateStopAllAxes(_wmx3Lib_cm);
            AddLog("🚨 긴급 중지(인터록) 요청");
        }

            // 인터록 복구 버튼
        private void BtnInterlockRecover_Click(object sender, RoutedEventArgs e)
        {
            if (!_interlockPending || _interlockRecovering) return;

            _interlockRecovering = true;
            BtnInterlockRecover.IsEnabled = false;
            AddLog("인터록 복구 시작");

            Thread recoverThread = new Thread(() =>
            {
                bool success = false;

                try
                {
                    CoreMotionStatus recoverStatus = new CoreMotionStatus();
                    RecoverFromInterlock(_wmx3Lib_cm, ref recoverStatus);
                    success = true;
                }
                catch (Exception ex)
                {
                    AddLog($"인터록 복구 중 오류: {ex.Message}");
                }

                _interlockRecovering = false;

                if (success)
                {
                    _interlockPending = false;
                    AddLog("인터록 복구 완료");
                    FinishAndResetUi();
                }
                else
                {
                              // 실패 시 재시도 가능하도록 버튼 다시 활성화
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BtnInterlockRecover.IsEnabled = true;
                    }));
                }
            })
            {
                IsBackground = true
            };
            recoverThread.Start();
        }

            // 미니맵 궤적 라인 초기화
        private void ClearTrajectory()
        {
            Dispatcher.Invoke(() =>
            {
                TrajectoryLine.Points.Clear();
            });
        }

            // 상태 머신 기반 웨이퍼 이송 루프 (새 스레드에서 실행)
        private void Wmx3MotionLoop()
        {
            try
            {
                        // 디바이스 초기화 (호밍, 서보 ON 등)
                InitializeDevice(Wmx3Lib, ref EnStatus, ref CmStatus);

                         // AxisStatusManager 생성 및 폴링 시작 (UI 업데이트용)
                _axisStatusManager = new AxisStatusManager(Wmx3Lib, _wmx3Lib_cm);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _axisStatusManager = new AxisStatusManager(Wmx3Lib, _wmx3Lib_cm);
                    _axisStatusManager.StartSnapshot(50);
                    _viewModel.AxisStatuses = _axisStatusManager.AxisStatuses;
                    StartStatusPolling();
                    AddLog("WMX3 연결됨 - 실시간 상태 모니터링 시작");
                }));

                         // 상태 머신: Starting → Running으로 전환
                _stateMachine._currentState = MotionState.Running; // Starting 상태에서 Running으로
                _stateMachine._currentStep = MotionStep.Idle;

                        // 주 제어 루프
                while (_motionThreadRunning && !_stateMachine.IsInterlocked())
                {
                                // 일시정지 체크
                    if (_stateMachine.CheckAndApplyPause())
                    {
                        AddLog($"[상태] 일시정지 - 현재 단계: {_stateMachine.CurrentStep}");
                                    // 모든 축 속도 0으로 설정 (Pause)
                        PauseAllAxes(_wmx3Lib_cm);

                                    // 일시정지 상태에서 대기
                        while (_stateMachine.CurrentState == MotionState.Paused && !_stateMachine.IsInterlocked())
                        {
                            Thread.Sleep(100);
                        }

                        if (_stateMachine.CurrentState == MotionState.Running)
                        {
                            AddLog($"[상태] 재개 - 현재 단계: {_stateMachine.CurrentStep}");
                        }
                    }

                              // 중단 체크
                    if (_stateMachine.ShouldStop())
                    {
                        AddLog("⏹ 중지 신호 감지");
                        break;
                    }

                              // 긴급 중지(인터록) 체크
                    if (_stateMachine.IsInterlocked())
                    {
                        AddLog("🚨 긴급 중지(인터록) 요청 감지");
                        ImmediateStopAllAxes(_wmx3Lib_cm);
                        break;
                    }

                               // 모션 단계별 처리
                    ExecuteMotionStep(_wmx3Lib_cm, ref CmStatus);

                               // 현재 시나리오 완료 후 반복 여부 결정
                    if (_stateMachine.CurrentStep == MotionStep.Completed)
                    {
                        if (_stateMachine.TryRestartCycle())
                        {
                            AddLog($"✓ 사이클 {_stateMachine.CycleCount} 완료 - 다음 사이클 시작");
                            
                            ClearTrajectory();
                            
                            Thread.Sleep(500); // 사이클 간 대기
                            continue;
                        }
                        else
                        {
                            AddLog("✓ 모든 작업 완료");
                            break;
                        }
                    }

                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                AddLog($"오류 발생: {ex.Message}");
            }
            finally
            {
                _motionThreadRunning = false;

                if (_stateMachine.IsInterlocked() && _deviceCreated)
                {
                              // 긴급정지: 서보 ON/파지 상태를 유지한 채 복구 버튼 입력을 대기
                    _interlockPending = true;
                    AddLog("🚨 인터록 상태 - '인터록 복구' 버튼으로 복구하세요.");

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BtnInterlockRecover.IsEnabled = true;
                    }));
                }
                else
                {
                               // 일반 중지: Start 지점으로 원점복귀 후 초기화
                    if (_deviceCreated)
                    {
                        ReturnToStartPosition(_wmx3Lib_cm, ref CmStatus);
                    }
                    FinishAndResetUi();
                }
            }
        }

            // 시작 전 상태로 복원
        private void FinishAndResetUi()
        {
            _axisStatusManager?.StopSnapshot();
            _axisStatusManager = null;

            try
            {
                CleanupDevice(Wmx3Lib);
            }
            catch (Exception ex)
            {
                AddLog($"초기화 중 오류: {ex.Message}");
            }

            _motionThreadRunning = false;
            _homingStarted = false;
            _waferGrasped = false;
            _stateMachine.Reset();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_statusPollingTimer != null)
                {
                    _statusPollingTimer.Stop();
                    _statusPollingTimer = null;
                }

                BtnInterlockRecover.IsEnabled = false;
                TrajectoryLine.Points.Clear();
                UpdateStagePositionOnCanvas(_startPositionMM);
                UpdateWaferPositionOnCanvas(_targetPositionMM);

                InitializeViewModel();
            }));
        }

        // 모션 단계별 실행
        private void ExecuteMotionStep(CoreMotion Wmx3Lib_cm, ref CoreMotionStatus CmStatus)
        {
            switch (_stateMachine.CurrentStep)
            {
                case MotionStep.Idle:
                    _stateMachine.AdvanceStep();
                    break;
                case MotionStep.ZAxisUp_Initial:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] Z축 서보모터 상승: 0mm → 50mm");
                    MoveZAxis(Wmx3Lib_cm, Z_AXIS_UP, "Z축 상승");
                    _stateMachine.AdvanceStep();
                    break;

                case MotionStep.MovingToTarget:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] 웨이퍼 위치(Target)로 이동");
                    MoveXYAxes(Wmx3Lib_cm, CmStatus, _targetPositionMM, 100000, "웨이퍼 적재소 도달");
                    _stateMachine.AdvanceStep();
                    break;

                case MotionStep.ZAxisDown_PickUp:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] Z축 서보모터 하강: 50mm → 0mm (픽업 준비)");
                    MoveZAxis(Wmx3Lib_cm, Z_AXIS_DOWN, "Z축 하강");
                    _stateMachine.AdvanceStep();
                    break;

                case MotionStep.GraspingWafer:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] 웨이퍼 그립");
                    Thread.Sleep(1000);
                    AddLog("웨이퍼 그립 완료");
                    _waferGrasped = true; 
                    _stateMachine.AdvanceStep();
                    break;

                case MotionStep.ZAxisUp_AfterPickUp:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] Z축 서보모터 상승: 0mm → 50mm (픽업 후)");
                    MoveZAxis(Wmx3Lib_cm, Z_AXIS_UP, "Z축 상승");
                    _stateMachine.AdvanceStep();
                    break;

                case MotionStep.MovingToGoal:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] 최종 목적지(Goal)로 이동");
                    MoveXYAxes(Wmx3Lib_cm, CmStatus, _goalPositionMM, 150000, "Goal 도달");
                    _stateMachine.AdvanceStep();
                    break;

                case MotionStep.ZAxisDown_Place:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] Z축 서보모터 하강: 50mm → 0mm (배치)");
                    MoveZAxis(Wmx3Lib_cm, Z_AXIS_DOWN, "Z축 하강");
                    _stateMachine.AdvanceStep();
                    break;

                case MotionStep.ReleasingWafer:
                    AddLog($"[사이클 {_stateMachine.CycleCount + 1}] 웨이퍼 해제");
                    Thread.Sleep(1000);
                    AddLog("웨이퍼 해제 완료");
                    _waferGrasped = false;
                    _stateMachine.AdvanceStep();
                    break;
            }
        }

            // 모든 축 일시정지
        private void PauseAllAxes(CoreMotion Wmx3Lib_cm)
        {
                  // 속도 Override를 0으로 설정하여 일시정지
            Wmx3Lib_cm.Motion.Pause(0);
            Wmx3Lib_cm.Motion.Pause(1);
            Wmx3Lib_cm.Motion.Pause(2);
        }

            // 모든 축 긴급 중지
        private void ImmediateStopAllAxes(CoreMotion Wmx3Lib_cm)
        {
            try
            {
                        // 각 축의 감속 파라미터를 긴급 감속도로 설정
                for (int i = 0; i < 3; i++)
                {
                    Config.MotionParam motionParam = new Config.MotionParam();
                    Wmx3Lib_cm.Config.GetMotionParam(i, ref motionParam);
                    motionParam.QuickStopDec = EMERGENCY_DECELERATION;
                    Wmx3Lib_cm.Config.SetMotionParam(i, motionParam);
                }
                
                Wmx3Lib_cm.Motion.ExecQuickStop(0);
                Wmx3Lib_cm.Motion.ExecQuickStop(1);
                Wmx3Lib_cm.Motion.ExecQuickStop(2);
                
                AddLog("긴급 정지 제어");
            }
            catch (Exception ex)
            {
                AddLog($"긴급 정지 중 오류: {ex.Message}");
            }
        }

            // 호밍 복귀
        private void ReturnToHome(CoreMotion Wmx3Lib_cm, ref CoreMotionStatus CmStatus)
        {
            AddLog("🏠 원점복귀 시작");
            
                  // 모든 축 호밍 실행
            for (int i = 0; i < 3; i++)
            {
                Config.HomeParam homeParam = new Config.HomeParam();
                Wmx3Lib_cm.Config.GetHomeParam(i, ref homeParam);
                homeParam.HomeType = Config.HomeType.CurrentPos;
                Wmx3Lib_cm.Config.SetHomeParam(i, homeParam);
                Wmx3Lib_cm.Home.StartHome(i);
            }

                   // 호밍 완료 대기
            while (true)
            {
                Wmx3Lib_cm.GetStatus(ref CmStatus);

                if (CmStatus.AxesStatus[0].HomeDone &&
                    CmStatus.AxesStatus[1].HomeDone &&
                    CmStatus.AxesStatus[2].HomeDone)
                {
                    AddLog("✓ 원점복귀 완료");
                    break;
                }

                Thread.Sleep(100);
            }

                   // 시작 위치로 이동
            AddLog($"시작 위치로 이동: ({_startPositionMM.X:F1}mm, {_startPositionMM.Y:F1}mm)");
            MoveXYAxes(Wmx3Lib_cm, CmStatus, _startPositionMM, 5000, "시작 위치 도착");
        }

            // 디바이스 초기화 (호밍, 서보 ON 등)
        private void InitializeDevice(WMX3Api Wmx3Lib, 
                                      ref EngineStatus EnStatus, ref CoreMotionStatus CmStatus)
        {
            AddLog("프로그램 시작");
            Thread.Sleep(1000);

                  // 디바이스 생성
            if (!_deviceCreated)
            {
                Wmx3Lib.CreateDevice("C:\\Program Files\\SoftServo\\WMX3\\", DeviceType.DeviceTypeNormal, 0xFFFFFFFF);
                Wmx3Lib.SetDeviceName("InterpolationMotion");
                _deviceCreated = true;
            }

                  // 서보 통신 시작
            Wmx3Lib.GetEngineStatus(ref EnStatus);

            if (EnStatus.State != EngineState.Communicating)
            {
                for (uint i = 0; i < 100; i++)
                {
                    Wmx3Lib.StartCommunication(0xFFFFFFFF);
                    Wmx3Lib.GetEngineStatus(ref EnStatus);

                    if (EnStatus.State == EngineState.Communicating)
                    {
                        AddLog("서보 통신 연결 완료");
                        break;
                    }
                }
            }
            else
            {
                AddLog("서보 통신 유지 중 - 재사용");
            }

                  // 모든 축 서보 ON
            _wmx3Lib_cm.AxisControl.SetServoOn(0, true);
            _wmx3Lib_cm.AxisControl.SetServoOn(1, true);
            _wmx3Lib_cm.AxisControl.SetServoOn(2, true);

            AddLog("서보 ON 명령 전송");

            while (true)
            {
                _wmx3Lib_cm.GetStatus(ref CmStatus);

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
            AddLog($"호밍 시작 위치로 이동: ({_startPositionMM.X:F1}mm, {_startPositionMM.Y:F1}mm)");
            MoveXYAxes(_wmx3Lib_cm, CmStatus, _startPositionMM, 5000, "호밍 시작 위치 이동");
            _homingStarted = true;

            for (int i = 0; i < 3; i++)
            {
                Config.HomeParam homeParam = new Config.HomeParam();
                _wmx3Lib_cm.Config.GetHomeParam(i, ref homeParam);
                homeParam.HomeType = Config.HomeType.CurrentPos;
                _wmx3Lib_cm.Config.SetHomeParam(i, homeParam);
                _wmx3Lib_cm.Home.StartHome(i);
            }

            AddLog("호밍(Homing) 시작");

            while (true)
            {
                _wmx3Lib_cm.GetStatus(ref CmStatus);

                if (CmStatus.AxesStatus[0].HomeDone &&
                    CmStatus.AxesStatus[1].HomeDone &&
                    CmStatus.AxesStatus[2].HomeDone)
                {
                    AddLog("호밍(Homing) 완료");
                    break;
                }

                Thread.Sleep(100);
            }
        }

            // 디바이스 정리 및 종료
        private void CleanupDevice(WMX3Api Wmx3Lib)
        {
                  // 모든 축 모션 정지 및 대기
            _wmx3Lib_cm.Motion.Wait(0);
            _wmx3Lib_cm.Motion.Wait(1);
            _wmx3Lib_cm.Motion.Wait(2);

                  // 서보 OFF
            _wmx3Lib_cm.AxisControl.SetServoOn(0, false);
            _wmx3Lib_cm.AxisControl.SetServoOn(1, false);
            _wmx3Lib_cm.AxisControl.SetServoOn(2, false);

            CoreMotionStatus CmStatus = new CoreMotionStatus();
            while (true)
            {
                _wmx3Lib_cm.GetStatus(ref CmStatus);
                if (!CmStatus.AxesStatus[0].ServoOn &&
                    !CmStatus.AxesStatus[1].ServoOn &&
                    !CmStatus.AxesStatus[2].ServoOn)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            AddLog("원점 복귀");
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
            _motionThreadRunning = false;
            _axisStatusManager?.StopSnapshot();

            if (_logFlushTimer != null)
            {
                _logFlushTimer.Stop();
                _logFlushTimer = null;
            }

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

            ShutdownDevice();
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


            // Z축을 목표 위치로 이동하는 메서드
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
            zAxisCmd.Profile.Velocity = 100000;   // Z축 속도 100,000 pulse/s = 약 300mm/s
            zAxisCmd.Profile.Acc = 200000;
            zAxisCmd.Profile.Dec = 200000;

            Wmx3Lib_cm.Motion.StartPos(zAxisCmd);
            AddLog($"Z축 이동 중 → 목표: {targetZ_mm:F1}mm");

            while (true)
            {
                        // 인터록 발생 시 대기 루프 종료
                if (ShouldAbortMotionWait())
                {
                    AddLog($"{stepName} 중단 - 인터록 발생");
                    return;
                }

                Wmx3Lib_cm.GetStatus(ref CmStatus);

                double currentZ_pulse = CmStatus.AxesStatus[Z_AXIS_INDEX].PosCmd;
                double currentZ_mm = (currentZ_pulse / PulsesPerStage) * StageMaxMM;

                if (CmStatus.AxesStatus[Z_AXIS_INDEX].InPos)
                {
                    AddLog($"{stepName} 완료 (Z={currentZ_mm:F2}mm)");
                    return;
                }

                Thread.Sleep(50);
            }
        }

             // X/Y축을 목표 위치로 이동하는 메서드
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
            AddLog($"X/Y축 서보모터 이동 중 → 목표: ({targetPos_mm.X:F1}mm, {targetPos_mm.Y:F1}mm)  속도: {velocityPps} pulse/s");

            while (true)
            {
                        // 인터록 발생 시 루프 종료
                if (ShouldAbortMotionWait())
                {
                    AddLog($"{stepName} 중단 - 인터록 발생");
                    return;
                }

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
                    AddLog($"{stepName} 완료 (X={currentX_mm:F2}mm, Y={currentY_mm:F2}mm)");
                    return;
                }

                Thread.Sleep(50);
            }
        }

            // 프로그램 종료 시에만 디바이스 해제
        private void ShutdownDevice()
        {
            if (!_deviceCreated)
                return;

            try
            {
                Wmx3Lib.StopCommunication(0xFFFFFFFF);
                Wmx3Lib.CloseDevice();
                _wmx3Lib_cm.Dispose();
                Wmx3Lib.Dispose();
            }
            catch (Exception ex)
            {
                AddLog($"디바이스 해제 중 오류: {ex.Message}");
            }
            finally
            {
                _deviceCreated = false;
            }
        }

            // 중지/긴급 정지 후 Start 지점으로 안전하게 원점복귀 (Z 상승 → XY 이동 → Z 하강)
        private void ReturnToStartPosition(CoreMotion Wmx3Lib_cm, ref CoreMotionStatus CmStatus)
        {
            AddLog("장비 원점복귀 시작 (Start 지점)");

            try
            {
                        // 일시정지/긴급 정지 상태의 축이 이동 명령을 받을 수 있도록 재개
                for (int i = 0; i < 3; i++)
                {
                    Wmx3Lib_cm.Motion.Resume(i);
                }

                        // 진행 중인 모션이 완전히 멈출 때까지 대기
                Wmx3Lib_cm.Motion.Wait(0);
                Wmx3Lib_cm.Motion.Wait(1);
                Wmx3Lib_cm.Motion.Wait(2);

                        // 1. Z축 상승 (이동 중 충돌 방지)
                MoveZAxis(Wmx3Lib_cm, Z_AXIS_UP, "원점복귀 Z축 상승");

                        // 2. X/Y축 Start 지점으로 이동
                MoveXYAxes(Wmx3Lib_cm, CmStatus, _startPositionMM, 50000, "원점복귀 X/Y 이동");

                        // 3. Z축 하강 (초기 상태)
                MoveZAxis(Wmx3Lib_cm, Z_AXIS_DOWN, "원점복귀 Z축 하강");

                AddLog("원점복귀 완료 - Start 지점 도착");
            }
            catch (Exception ex)
            {
                AddLog($"원점복귀 중 오류: {ex.Message}");
            }
        }

            // 인터록 복구 시퀀스
            //  - 웨이퍼 흡착 중: 웨이퍼 공급소로 이동 → Z축 서보모터 하강 → 웨이퍼 내려놓기 → Z 서보모터 상승 → 시작지점 복귀
            //  - 웨이퍼 미흡착 중: 시작 지점으로 이동
        private void RecoverFromInterlock(CoreMotion Wmx3Lib_cm, ref CoreMotionStatus CmStatus)
        {
                  // 서보모터 모션 종료 대기
            for (int i = 0; i < 3; i++)
            {
                Wmx3Lib_cm.Motion.Resume(i);
            }

            Wmx3Lib_cm.Motion.Wait(0);
            Wmx3Lib_cm.Motion.Wait(1);
            Wmx3Lib_cm.Motion.Wait(2);

                  // 이동 중 충돌 방지를 위한 Z축 상승
                  //MoveZAxis(Wmx3Lib_cm, Z_AXIS_UP, "[Repair] Z축 상승");

            if (_waferGrasped)
            {
                AddLog("[Repair] 웨이퍼 흡착 State - 웨이퍼 공급소 이동.");

                MoveXYAxes(Wmx3Lib_cm, CmStatus, _targetPositionMM, 50000, "[Repair]  웨이퍼 공급소 도달");
                MoveZAxis(Wmx3Lib_cm, Z_AXIS_DOWN, "[Repair] Z축 하강 (웨이퍼 재배치)");

                        // 웨이퍼 탈착
                _waferGrasped = false;
                Dispatcher.Invoke(() =>
                {
                    UpdateWaferPositionOnCanvas(_targetPositionMM);
                });
                AddLog("[Repair] 웨이퍼 공급소에 재배치 완료");

                MoveZAxis(Wmx3Lib_cm, Z_AXIS_UP, "[Repair] Z축 상승");
            }
            else
            {
                AddLog("[Repair] 웨이퍼 미흡착 State - 시작 지점으로 이동");
            }

                  // 원점복귀
            MoveXYAxes(Wmx3Lib_cm, CmStatus, _startPositionMM, 50000, "[Repair] Start 지점 도달");
            MoveZAxis(Wmx3Lib_cm, Z_AXIS_DOWN, "[Repair] 복구 Z축 하강");
        }

            // 인터록 발생 시 모션 대기 시퀀스 중단 여부 - 복구 동작 중에는 미중단
        private bool ShouldAbortMotionWait()
        {
            return _stateMachine.IsInterlocked() && !_interlockRecovering;
        }
    }
}
