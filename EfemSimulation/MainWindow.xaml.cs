using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace EfemSimulation
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window
    {
        private DispatcherTimer _simTimer;

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

        public MainWindow()
        {
            InitializeComponent();
            InitSimulationEnvironment();
        }

        private void InitSimulationEnvironment()
        {
            // 1. 그래프 초기화
            _xStreamer = SpeedPlot.Plot.Add.DataStreamer(500);
            _yStreamer = SpeedPlot.Plot.Add.DataStreamer(500);
            _xStreamer.Color = ScottPlot.Color.FromHex("#FF5733"); // X축 모터: 주황
            _yStreamer.Color = ScottPlot.Color.FromHex("#33FF57"); // Y축 모터: 녹색
            _xStreamer.Label = "X-Axis Motor Speed";
            _yStreamer.Label = "Y-Axis Motor Speed";

            SpeedPlot.Plot.ShowLegend(ScottPlot.Alignment.UpperRight);
            SpeedPlot.Plot.Axes.SetLimitsY(-10, 500); // 속도 영역 가시화
            SpeedPlot.Refresh();

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
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            _t = 0.0;
            _currentMM = _startMM;
            TrajectoryLine.Points.Clear();
            _simTimer.Start();
        }

        private void SimulationLoop(object sender, EventArgs e)
        {
            _t += 0.01; // 이동 속도 제어 변수
            if (_t > 1.0)
            {
                _t = 1.0;
                _simTimer.Stop();
                MessageBox.Show("웨이퍼 이송 완료!", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
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

            // 그래프 업데이트
            _xStreamer.Add(xSpeed);
            _yStreamer.Add(ySpeed);
            SpeedPlot.Refresh();

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
    }
}
