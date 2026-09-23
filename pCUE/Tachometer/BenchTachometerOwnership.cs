using System;
using System.Threading;
using System.Threading.Tasks;

namespace pCUE
{
    // Windows mutex ownership is thread-affine. This dedicated thread holds it for the
    // complete HID connection, regardless of which thread opens or closes the meter.
    public sealed class BenchTachometerOwnership : IDisposable
    {
        public const string SharedName = @"Global\Cybenetics.BenchTachometer.1A86_E008";
        private readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
        private readonly Thread thread;
        private int disposed;
        private BenchTachometerOwnership(string name)
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            thread = new Thread(() =>
            {
                try
                {
                    using (var mutex = new Mutex(false, name))
                    {
                        bool owned;
                        try { owned = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { owned = true; }
                        ready.TrySetResult(owned);
                        if (!owned) return;
                        try { release.Wait(); }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true, Name = "Bench tach ownership" };
            thread.Start();
            try
            {
                if (!ready.Task.GetAwaiter().GetResult())
                    throw new InvalidOperationException("The bench tachometer is owned by another application. Disconnect it there first.");
            }
            catch { Dispose(); throw; }
        }
        public static BenchTachometerOwnership Acquire(string name = SharedName) { return new BenchTachometerOwnership(name); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            release.Set();
            if (Thread.CurrentThread != thread) thread.Join(2000);
            try { release.Dispose(); } catch { }
        }
    }
}
