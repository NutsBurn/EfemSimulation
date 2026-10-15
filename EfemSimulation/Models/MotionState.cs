using System;

namespace EfemSimulation.Models
{

      // 웨이퍼 이송 상태
    public enum MotionState
    {
        Idle,           // 대기 중
        Starting,       // 시작 중 (호밍, 서보 ON 등)
        Running,        // 이송 중
        Paused,         // 일시정지 중
        Stopping,       // 중지 중 (현재 시나리오 완료 후 반복 중단)
        Interlocking,   // 긴급 중지(비상 정지)
        Completed       // 모든 작업 완료
    }

      // 웨이퍼 이송 현재 단계
    public enum MotionStep
    {
        Idle,
        Homing,
        ZAxisUp_Initial,
        MovingToTarget,
        ZAxisDown_PickUp,
        GraspingWafer,
        ZAxisUp_AfterPickUp,
        MovingToGoal,
        ZAxisDown_Place,
        ReleasingWafer,
        Completed
    }

      // 웨이퍼 이송 모션 상태 머신
    public class MotionStateMachine
    {
            // 현재 상태 및 단계
        internal MotionState _currentState = MotionState.Idle;
        internal MotionStep _currentStep = MotionStep.Idle;

            // 제어 신호
        private bool _pauseRequested = false;
        private bool _stopRequested = false;
        private bool _interlockRequested = false;
        private bool _resumeRequested = false;

            // 반복 제어
        private bool _repeatEnabled = true;
        private int _cycleCount = 0;

        public MotionState CurrentState => _currentState;
        public MotionStep CurrentStep => _currentStep;
        public int CycleCount => _cycleCount;
        public bool IsRunning => _currentState == MotionState.Running;
        public bool IsPaused => _currentState == MotionState.Paused;

            // 이송 시작 요청
        public void RequestStart()
        {
            if (_currentState == MotionState.Idle)
            {
                _currentState = MotionState.Starting;
                _currentStep = MotionStep.Homing;
                _pauseRequested = false;
                _stopRequested = false;
                _interlockRequested = false;
                _resumeRequested = false;
                _repeatEnabled = true;
            }
        }

            // 이송 일시정지 요청
        public void RequestPause()
        {
            if (_currentState == MotionState.Running)
            {
                _pauseRequested = true;
            }
        }

            // 일시정지된 이송 재개 요청
        public void RequestResume()
        {
            if (_currentState == MotionState.Paused)
            {
                _resumeRequested = true;
                _currentState = MotionState.Running;
            }
        }

            // 현재 사이클 완료 후 반복 중단 요청
        public void RequestStop()
        {
            if (_currentState == MotionState.Running || _currentState == MotionState.Paused)
            {
                _stopRequested = true;
                _repeatEnabled = false;
            }
        }

            // 긴급 중지(비상 정지) 요청 - 즉시 모든 모션 중단
        public void RequestInterlock()
        {
            _interlockRequested = true;
            _currentState = MotionState.Interlocking;
            _repeatEnabled = false;
        }

            // 일시정지 요청 확인 및 상태 업데이트
        public bool CheckAndApplyPause()
        {
            if (_pauseRequested)
            {
                _currentState = MotionState.Paused;
                _pauseRequested = false;
                return true;
            }
            return false;
        }

            // 모션 단계 진행
        public void AdvanceStep()
        {
            if (_currentState == MotionState.Running)
            {
                switch (_currentStep)
                {
                    case MotionStep.Idle:
                        _currentStep = MotionStep.ZAxisUp_Initial;
                        break;
                    case MotionStep.ZAxisUp_Initial:
                        _currentStep = MotionStep.MovingToTarget;
                        break;
                    case MotionStep.MovingToTarget:
                        _currentStep = MotionStep.ZAxisDown_PickUp;
                        break;
                    case MotionStep.ZAxisDown_PickUp:
                        _currentStep = MotionStep.GraspingWafer;
                        break;
                    case MotionStep.GraspingWafer:
                        _currentStep = MotionStep.ZAxisUp_AfterPickUp;
                        break;
                    case MotionStep.ZAxisUp_AfterPickUp:
                        _currentStep = MotionStep.MovingToGoal;
                        break;
                    case MotionStep.MovingToGoal:
                        _currentStep = MotionStep.ZAxisDown_Place;
                        break;
                    case MotionStep.ZAxisDown_Place:
                        _currentStep = MotionStep.ReleasingWafer;
                        break;
                    case MotionStep.ReleasingWafer:
                        _currentStep = MotionStep.Completed;
                        break;
                }
            }
        }

            // 현재 사이클 완료 후 호출 (반복 여부 결정)
        public bool TryRestartCycle()
        {
            if (_currentStep == MotionStep.Completed && _repeatEnabled && !_stopRequested)
            {
                _cycleCount++;
                _currentState = MotionState.Running;
                _currentStep = MotionStep.Idle;
                return true;
            }

            _currentState = MotionState.Completed;
            return false;
        }

            // 중단/반복 중단 여부 확인
        public bool ShouldStop()
        {
            return _stopRequested;
        }

            // 긴급 정지 여부 확인
        public bool IsInterlocked()
        {
            return _interlockRequested;
        }

            // 상태 초기화
        public void Reset()
        {
            _currentState = MotionState.Idle;
            _currentStep = MotionStep.Idle;
            _pauseRequested = false;
            _stopRequested = false;
            _interlockRequested = false;
            _resumeRequested = false;
            _repeatEnabled = true;
            _cycleCount = 0;
        }
    }
}