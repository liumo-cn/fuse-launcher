using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Pipeline;

/// <summary>管线进度：Stage 为当前节点名，Progress 为整条管线 0~1 的加权进度。</summary>
public sealed record LoaderProgress(string Stage, double Progress, string? Message = null);

/// <summary>一个有输入输出、可取消、带进度的异步任务节点（借鉴常见任务管线抽象）。</summary>
public interface ILoaderTask<in TIn, TOut>
{
    string Name { get; }

    Task<TOut> RunAsync(TIn input, IProgress<LoaderProgress> progress, CancellationToken cancellation);
}

/// <summary>
/// 顺序任务编排：按权重汇总进度，任一节点抛出或取消则整条失败。
/// 各节点通过 apply 回调把结果写回共享上下文，避免 object 装箱链。
/// </summary>
public sealed class LoaderCombo<TContext>
{
    private sealed class Step
    {
        public required string Name;
        public required double Weight;
        public required Func<TContext, IProgress<LoaderProgress>, CancellationToken, Task> Run;
    }

    private readonly List<Step> _steps = new();

    public IReadOnlyList<string> StepNames => _steps.ConvertAll(s => s.Name);

    public LoaderCombo<TContext> Then<TOut>(
        ILoaderTask<TContext, TOut> task,
        double weight,
        Action<TContext, TOut> apply)
    {
        _steps.Add(new Step
        {
            Name = task.Name,
            Weight = weight,
            Run = async (ctx, progress, ct) =>
            {
                var result = await task.RunAsync(ctx, progress, ct).ConfigureAwait(false);
                apply(ctx, result);
            },
        });
        return this;
    }

    /// <summary>把一个普通委托包装为节点，便于内联小步骤。</summary>
    public LoaderCombo<TContext> Then(string name, double weight, Func<TContext, CancellationToken, Task> body)
    {
        _steps.Add(new Step { Name = name, Weight = weight, Run = (ctx, _, ct) => body(ctx, ct) });
        return this;
    }

    public async Task RunAsync(TContext context, IProgress<LoaderProgress>? progress, CancellationToken cancellation)
    {
        double totalWeight = 0;
        foreach (var s in _steps) totalWeight += s.Weight;
        if (totalWeight <= 0) totalWeight = 1;

        double finished = 0;
        foreach (var step in _steps)
        {
            cancellation.ThrowIfCancellationRequested();
            var stepProgress = new Progress<LoaderProgress>(p =>
                progress?.Report(new LoaderProgress(step.Name, (finished + step.Weight * p.Progress) / totalWeight, p.Message)));
            try
            {
                await step.Run(context, stepProgress, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LoaderStepException(step.Name, ex);
            }
            finished += step.Weight;
            progress?.Report(new LoaderProgress(step.Name, finished / totalWeight));
        }
    }
}

/// <summary>失败时携带节点名，UI/日志可据此定位到具体步骤。</summary>
public sealed class LoaderStepException : Exception
{
    public string StepName { get; }

    public LoaderStepException(string stepName, Exception inner)
        : base($"任务节点「{stepName}」失败：{inner.Message}", inner)
    {
        StepName = stepName;
    }
}
