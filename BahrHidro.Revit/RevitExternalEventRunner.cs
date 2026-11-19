using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    public sealed class RevitExternalEventRunner : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new();
        private readonly ExternalEvent _externalEvent;

        public RevitExternalEventRunner()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        public Task<T> RunAsync<T>(Func<UIApplication, T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _queue.Enqueue(app =>
            {
                try
                {
                    T result = func(app);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            _externalEvent.Raise();
            return tcs.Task;
        }

        public Task RunAsync(Action<UIApplication> action)
        {
            return RunAsync<bool>(app =>
            {
                action(app);
                return true;
            });
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var work))
            {
                work(app);
            }
        }

        public string GetName() => "Bahr Hidro - Executor Assíncrono BIM";
    }
}
