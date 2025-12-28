using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Revive.Slime
{
    /// <summary>
    /// 性能分析器 - 用于定位卡顿原因
    /// 使用方法：
    /// 1. 在代码段前调用 PerformanceProfiler.Begin("阶段名称")
    /// 2. 在代码段后调用 PerformanceProfiler.End("阶段名称")
    /// 3. 每帧结束时调用 PerformanceProfiler.EndFrame()
    /// </summary>
    public static class PerformanceProfiler
    {
        #region 配置
        
        /// <summary>是否启用性能分析（可在运行时切换）</summary>
        public static bool Enabled = true;
        
        /// <summary>帧时间超过此阈值(ms)时输出警告</summary>
        public static float FrameTimeWarningThreshold = 20f; // 约50FPS以下
        
        /// <summary>单阶段超过此阈值(ms)时标记为慢</summary>
        public static float StageTimeWarningThreshold = 5f;
        
        /// <summary>每隔多少帧输出一次汇总统计</summary>
        public static int SummaryInterval = 300;
        
        /// <summary>是否在每帧都输出详细日志</summary>
        public static bool VerboseMode = false;
        
        /// <summary>是否只在卡顿帧输出详细日志</summary>
        public static bool LogOnlySlowFrames = true;
        
        #endregion
        
        #region 内部数据
        
        private struct StageData
        {
            public double CurrentMs;
            public double TotalMs;
            public double MaxMs;
            public int CallCount;
            public Stopwatch Timer;
        }
        
        private static Dictionary<string, StageData> _stages = new Dictionary<string, StageData>();
        private static Stopwatch _frameTimer = new Stopwatch();
        private static int _frameCount = 0;
        private static double _totalFrameTime = 0;
        private static double _maxFrameTime = 0;
        private static int _slowFrameCount = 0;

        private const int EndWithoutBeginWarningIntervalFrames = 120;
        private const int SlowFrameDetailLogIntervalFrames = 30;

        private static readonly Dictionary<string, int> _endWithoutBeginLastWarningFrame = new Dictionary<string, int>();
        private static int _lastSlowFrameDetailLogProfilerFrame = -999999;
        private static int _lastSlowFrameDetailSignature = 0;
        private static bool _wasSlowFrameLastProfilerFrame = false;
        
        // 当前帧的阶段执行顺序
        private static List<string> _currentFrameStages = new List<string>();
        
        // 嵌套计时栈
        private static Stack<string> _activeStages = new Stack<string>();
        
        #endregion
        
        #region 公共 API
        
        /// <summary>
        /// 开始计时一个阶段
        /// </summary>
        public static void Begin(string stageName)
        {
            if (!Enabled) return;
            
            if (!_stages.TryGetValue(stageName, out var data))
            {
                data = new StageData
                {
                    Timer = new Stopwatch(),
                    CurrentMs = 0,
                    TotalMs = 0,
                    MaxMs = 0,
                    CallCount = 0
                };
                _stages[stageName] = data;
            }
            
            data.Timer.Restart();
            _stages[stageName] = data;
            _activeStages.Push(stageName);
            
            if (!_currentFrameStages.Contains(stageName))
                _currentFrameStages.Add(stageName);
        }
        
        /// <summary>
        /// 结束计时一个阶段
        /// </summary>
        public static void End(string stageName)
        {
            if (!Enabled) return;
            
            if (!_stages.TryGetValue(stageName, out var data))
            {
                int currentFrame = Time.frameCount;
                if (_endWithoutBeginLastWarningFrame.TryGetValue(stageName, out int lastWarnFrame) &&
                    currentFrame - lastWarnFrame < EndWithoutBeginWarningIntervalFrames)
                {
                    return;
                }

                _endWithoutBeginLastWarningFrame[stageName] = currentFrame;
                Debug.LogWarning($"[Profiler] End() 调用了未 Begin() 的阶段: {stageName}");
                return;
            }
            
            data.Timer.Stop();
            double elapsed = data.Timer.Elapsed.TotalMilliseconds;
            data.CurrentMs = elapsed;
            data.TotalMs += elapsed;
            data.MaxMs = Math.Max(data.MaxMs, elapsed);
            data.CallCount++;
            _stages[stageName] = data;
            
            if (_activeStages.Count > 0 && _activeStages.Peek() == stageName)
                _activeStages.Pop();
        }
        
        /// <summary>
        /// 开始新的一帧（在 Update 或 FixedUpdate 开头调用）
        /// </summary>
        public static void BeginFrame()
        {
            if (!Enabled) return;
            
            _currentFrameStages.Clear();
            _frameTimer.Restart();
            
            // 重置当前帧的阶段计时
            var keys = new List<string>(_stages.Keys);
            foreach (var key in keys)
            {
                var data = _stages[key];
                data.CurrentMs = 0;
                _stages[key] = data;
            }
        }
        
        /// <summary>
        /// 结束当前帧（在 Update 或 FixedUpdate 结尾调用）
        /// </summary>
        public static void EndFrame()
        {
            if (!Enabled) return;
            
            _frameTimer.Stop();
            double frameTime = _frameTimer.Elapsed.TotalMilliseconds;
            _frameCount++;
            _totalFrameTime += frameTime;
            _maxFrameTime = Math.Max(_maxFrameTime, frameTime);
            
            bool isSlowFrame = frameTime > FrameTimeWarningThreshold;
            if (isSlowFrame)
                _slowFrameCount++;
            
            // 输出详细日志
            if (VerboseMode)
            {
                OutputFrameDetails(frameTime, isSlowFrame);
            }
            else if (LogOnlySlowFrames && isSlowFrame)
            {
                if (ShouldLogSlowFrameDetails(frameTime))
                    OutputFrameDetails(frameTime, true);
            }

            _wasSlowFrameLastProfilerFrame = isSlowFrame;
            
            // 定期输出汇总
            if (_frameCount % SummaryInterval == 0)
            {
                OutputSummary();
            }
        }
        
        /// <summary>
        /// 重置所有统计数据
        /// </summary>
        public static void Reset()
        {
            _stages.Clear();
            _endWithoutBeginLastWarningFrame.Clear();
            _frameCount = 0;
            _totalFrameTime = 0;
            _maxFrameTime = 0;
            _slowFrameCount = 0;
            _lastSlowFrameDetailLogProfilerFrame = -999999;
            _lastSlowFrameDetailSignature = 0;
            _wasSlowFrameLastProfilerFrame = false;
            _currentFrameStages.Clear();
            _activeStages.Clear();
        }
        
        /// <summary>
        /// 手动输出当前汇总
        /// </summary>
        public static void OutputSummary()
        {
            if (_frameCount == 0) return;
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"========== 性能分析汇总 (累计 {_frameCount} 帧) ==========");
            sb.AppendLine($"帧时间: 平均={_totalFrameTime / _frameCount:F2}ms, 最大={_maxFrameTime:F2}ms");
            sb.AppendLine($"卡顿帧: {_slowFrameCount} ({100f * _slowFrameCount / _frameCount:F1}%)");
            sb.AppendLine("各阶段耗时:");
            
            // 按总耗时排序
            var sortedStages = new List<KeyValuePair<string, StageData>>(_stages);
            sortedStages.Sort((a, b) => b.Value.TotalMs.CompareTo(a.Value.TotalMs));
            
            foreach (var kvp in sortedStages)
            {
                if (kvp.Value.CallCount == 0) continue;
                
                double avgPerCallMs = kvp.Value.TotalMs / kvp.Value.CallCount;
                double avgPerFrameMs = kvp.Value.TotalMs / _frameCount;
                double callsPerFrame = (double)kvp.Value.CallCount / _frameCount;
                string marker = kvp.Value.MaxMs > StageTimeWarningThreshold ? " ★慢★" : "";
                sb.AppendLine($"  [{kvp.Key}]: 总计={kvp.Value.TotalMs:F1}ms, 每帧={avgPerFrameMs:F2}ms, 单次均值={avgPerCallMs:F2}ms, 最大={kvp.Value.MaxMs:F2}ms, 调用={kvp.Value.CallCount}次({callsPerFrame:F2}/帧){marker}");
            }
            
            sb.AppendLine("================================================");
        }
        
        #endregion
        
        #region 私有方法

        private static bool ShouldLogSlowFrameDetails(double frameTime)
        {
            int signature = CalculateFrameSignature(frameTime);
            bool isTransition = !_wasSlowFrameLastProfilerFrame;
            bool isIntervalElapsed = _frameCount - _lastSlowFrameDetailLogProfilerFrame >= SlowFrameDetailLogIntervalFrames;
            bool isDifferent = signature != _lastSlowFrameDetailSignature;

            if (isTransition || isIntervalElapsed || isDifferent)
            {
                _lastSlowFrameDetailLogProfilerFrame = _frameCount;
                _lastSlowFrameDetailSignature = signature;
                return true;
            }

            return false;
        }

        private static int CalculateFrameSignature(double frameTime)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) ^ (int)(frameTime * 10);

                foreach (var stageName in _currentFrameStages)
                {
                    if (_stages.TryGetValue(stageName, out var data) && data.CurrentMs > 0)
                    {
                        hash = (hash * 31) ^ stageName.GetHashCode();
                        hash = (hash * 31) ^ (int)(data.CurrentMs * 10);
                    }
                }

                return hash;
            }
        }
        
        private static void OutputFrameDetails(double frameTime, bool isSlowFrame)
        {
            var sb = new System.Text.StringBuilder();
            
            if (isSlowFrame)
                sb.Append($"<color=red>[卡顿帧]</color> ");
            else
                sb.Append("[帧详情] ");
            
            sb.Append($"帧#{Time.frameCount}({_frameCount}) 总耗时={frameTime:F2}ms | ");
            
            foreach (var stageName in _currentFrameStages)
            {
                if (_stages.TryGetValue(stageName, out var data) && data.CurrentMs > 0)
                {
                    string color = data.CurrentMs > StageTimeWarningThreshold ? "red" : "white";
                    sb.Append($"<color={color}>{stageName}={data.CurrentMs:F2}ms</color> ");
                }
            }
        }
        
        #endregion
        
        #region 便捷方法
        
        /// <summary>
        /// 使用 using 语句自动计时
        /// 用法: using (PerformanceProfiler.Scope("阶段名")) { ... }
        /// </summary>
        public static ProfilerScope Scope(string stageName)
        {
            return new ProfilerScope(stageName);
        }
        
        public struct ProfilerScope : IDisposable
        {
            private readonly string _name;
            private readonly bool _enabled;
            
            public ProfilerScope(string name)
            {
                _name = name;
                _enabled = Enabled;
                if (_enabled) Begin(name);
            }
            
            public void Dispose()
            {
                if (_enabled) End(_name);
            }
        }
        
        #endregion
    }
}
