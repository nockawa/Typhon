using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

[PublicAPI]
public class ThreadWorkers : IDisposable
{
    private readonly ILogger _logger;

    public class Context
    {
        public int Stage;
        public int ThreadId;
    }

    private readonly Dictionary<int, Action<Context>[]> _stages;
    private int _maxStageNumber;

    public ThreadWorkers(ILogger logger)
    {
        _logger = logger;
        _stages = new Dictionary<int, Action<Context>[]>();
    }

    public void AddStage(int stageNumber, int threadIdStart, Action<Context> action, int threadCount=1)
    {
        for (int i = 0; i < threadCount; i++)
        {
            if (!_stages.TryGetValue(threadIdStart + i, out var stages))
            {
                stages = new Action<Context>[Math.Max(8, stageNumber + 1)];
                _stages.Add(threadIdStart + i, stages);
            }

            if (stageNumber >= stages.Length)
            {
                Array.Resize(ref stages, stageNumber + 8);
                _stages[threadIdStart + i] = stages;
            }

            _maxStageNumber = Math.Max(_maxStageNumber, stageNumber + 1);
            stages[stageNumber] = action;
        }
    }

    public void Run()
    {
        var contexts = new Dictionary<int, Context>();
        var tasks = new List<Task>();
        for (int i = 0; i < _maxStageNumber; i++)
        {
            var stage = i;
            _logger.LogInformation("[{DateTime}] Run Stage {I}", DateTime.UtcNow, i);
            tasks.Clear();

            foreach (var kvp in _stages)
            {
                var threadId = kvp.Key;

                if (!contexts.TryGetValue(kvp.Key, out var c))
                {
                    c = new Context();
                    contexts.Add(kvp.Key, c);
                }

                c.ThreadId = threadId;
                c.Stage = stage;

                var actions = kvp.Value;
                if (actions[i] != null)
                {
                    tasks.Add(Task.Run(() => actions[stage](c)));
                }
            }

            Task.WaitAll(tasks.ToArray());
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
            
        _stages.Clear();
        IsDisposed = true;
        GC.SuppressFinalize(this);
    }

    public bool IsDisposed { get; private set; }
}